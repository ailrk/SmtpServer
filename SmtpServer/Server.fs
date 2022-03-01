module SMTP


open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open MimeKit
open System.Threading.Tasks
open System.Threading
open System.Text.RegularExpressions
open System.Reactive.Subjects
open System.Reactive
open System.Reactive.Linq


type private Actor<'T> = MailboxProcessor<'T>


type Email = Email of MimeMessage

type EmailHandler = Email seq -> unit

type CheckInbox =
    | Get of AsyncReplyChannel<Email seq>
    | GetAndReset of AsyncReplyChannel<Email seq>
    | Add of Email


type Message =
    | Received of string
    | Sent of string



type ReaderLines =
    | Read of AsyncReplyChannel<string>
    | Write of string
    | GetAll of AsyncReplyChannel<Message list>
    | End


module ReaderWriter =


    type t =
        { read: unit -> string
          write: string -> unit
          getAll: unit -> Message list }


    let private actor (sr: StreamReader, wr: StreamWriter) =
        Actor.Start (fun inbox ->
            let rec loop lines =
                async {
                    let! msg = inbox.Receive()

                    match msg with
                    | Read chan ->
                        let line = sr.ReadLine()
                        chan.Reply line
                        return! loop (Received(line) :: lines)
                    | Write msg ->
                        wr.WriteLine(msg)
                        return! loop (Sent(msg) :: lines)
                    | GetAll chan ->
                        lines |> List.rev |> chan.Reply
                        return! loop lines
                    | End ->
                        sr.Dispose()
                        wr.Dispose()
                }

            loop [])

    let create (stream: Stream) : t =
        let sr = new StreamReader(stream)
        let wr = new StreamWriter(stream)
        wr.NewLine <- "\r\n"
        wr.AutoFlush <- true
        let actor = actor <| (sr, wr)

        { read = fun () -> actor.PostAndReply Read
          write = fun line -> actor.Post(Write line)
          getAll = fun () -> actor.PostAndReply GetAll }


let private (|DATA|QUIT|HELLO|EHLO|NOOP|MAILFROM|RCPTTO|) (input: string) =

    match input.Split [| ':' |] |> Array.toList with
    | cmd :: [] ->
        match cmd |> String.map Char.ToUpper with
        | "DATA" -> DATA
        | "QUIT" -> QUIT
        | "HELLO" -> HELLO
        | "EHLO" -> EHLO
        | _ -> NOOP
    | field :: address :: [] ->
        match field
              |> fun s -> s.Trim()
              |> String.map Char.ToUpper
            with
        | "MAIL FROM" -> MAILFROM address
        | "RCPT TO" -> RCPTTO address
        | _ -> NOOP
    | _ -> NOOP

let readUntilTerminator (readline: unit -> string) =
    ()
    |> Seq.unfold (fun _ -> Some(readline (), ()))
    |> Seq.takeWhile (fun line -> not (line = null || line.Trim() = "."))


let smtpHandler (rw: ReaderWriter.t) =
    let rec readlines email : Result<Email, string> =
        match rw.read () with
        | DATA ->
            rw.write "354 Start input, end data with <CRLF>.<CRLF>"

            let data =
                try
                    readUntilTerminator rw.read
                    |> String.concat "\r\n"
                    |> System.Text.Encoding.ASCII.GetBytes
                    |> fun msg -> new MemoryStream(msg)
                    |> MimeKit.MimeMessage.Load
                    |> Email
                    |> Some
                with
                | _ -> None

            readlines data

        | QUIT ->
            rw.write "332 OK, Servie closing the socket, ciao"

            match email with
            | Some e ->
                rw.write $"250 OK, receved"
                Result.Ok e
            | None ->
                rw.write $"500 Erro when reading from the DATA command"
                Result.Error "[smtp] error when readingDATA section"

        | HELLO ->
            rw.write "220 HELLO there"
            None |> readlines

        | EHLO ->
            rw.write "220 EHLO there"
            None |> readlines

        | MAILFROM fromAddr ->
            rw.write $"250 OK, mail from {fromAddr}"
            None |> readlines

        | RCPTTO toAddr ->
            rw.write $"250 OK, rcpt to {toAddr}"
            None |> readlines

        | other ->
            rw.write $"502 Command is not implemented: {other}"
            None |> readlines

    readlines <| None

let receive (listener: TcpListener) (cancellation: CancellationToken) =
    task {
        let cancellationTask = TaskCompletionSource()

        let cancellationEvent =
            cancellation.Register(fun _ -> cancellationTask.SetCanceled())

        let acceptClientTask = listener.AcceptTcpClientAsync()

        do!
            Task.WhenAll [ cancellationTask.Task
                           acceptClientTask ]
            |> Async.AwaitTask
            |> Async.Ignore

        if cancellationTask.Task.IsCanceled then
            return Result.Error "recive email is canceled"
        else
            use! client = acceptClientTask |> Async.AwaitTask
            let stream = client.GetStream()
            let rw = ReaderWriter.create stream
            let data = smtpHandler rw
            let messages = rw.getAll ()
            client.Close()

            return
                data
                |> Result.bind (fun email -> Result.Ok(email, messages))

    }

type public ForwardServerInfo = { Port: int; Host: string }

let forward (forwardServer: ForwardServerInfo) (messages: Message list) =
    task {
        use client = new TcpClient()
        let port = forwardServer.Port
        let host = forwardServer.Host
        do! client.ConnectAsync(IPAddress.Parse host, port)
        use stream = client.GetStream()
        use w = new StreamWriter(stream)
        w.NewLine <- "\r\n"
        w.AutoFlush <- true
        use r = new StreamReader(stream)


        for msg in messages do
            match msg with
            | Received m -> do! w.WriteLineAsync m
            | Sent _ ->
                do!
                    r.ReadLineAsync()
                    |> Async.AwaitTask
                    |> Async.Ignore

        client.Close()
    }


let smtpActor
    (cacheActor: Actor<CheckInbox>)
    (port: int)
    (forwardServerInfo: ForwardServerInfo option)
    (cancellation: CancellationToken)
    =
    Actor.Start
    <| fun _ ->
        let server =
            new TcpListener(new IPEndPoint(IPAddress.Any, port))

        server.Start()

        let rec loop () =
            task {
                let! result = receive server cancellation |> Async.AwaitTask

                match result with
                | Result.Error _ -> server.Stop()
                | Result.Ok (email, messages) ->
                    email |> Add |> cacheActor.Post

                    match forwardServerInfo with
                    | Some info -> do! forward info messages
                    | _ -> ()

                if cancellation.IsCancellationRequested then
                    server.Stop()
                else
                    return! loop ()

            }

        loop () |> Async.AwaitTask



let cacheActor emailCont =
    Actor.Start
    <| fun inbox ->
        let rec loop messages =
            async {

                let! newMessage = inbox.Receive()

                match newMessage with
                | Get chan ->
                    chan.Reply messages
                    return! loop messages
                | GetAndReset chan ->
                    chan.Reply messages
                    return! loop []
                | Add message ->
                    message |> emailCont
                    return! loop (message :: messages)
            }

        loop []



module Server =

    type t =
        { getEmails: bool -> Email seq
          port: int
          emailReceived: IObservable<Email> }

    let create (port: int) (forwardServerInfo: ForwardServerInfo option) =
        let cancellationSource = new CancellationTokenSource()
        let emailReceived = new Subject<Email>()
        let cache = cacheActor emailReceived.OnNext

        let server =
            smtpActor cache port forwardServerInfo cancellationSource.Token

        { getEmails = fun reset -> cache.PostAndReply Get

          port = port

          emailReceived = emailReceived.AsObservable() }

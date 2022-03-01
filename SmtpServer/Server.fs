module SMTP

open System.Net.Sockets
open System.Net
open System.IO
open System
open System.Threading
open System.Threading.Tasks
open System.Reactive
open System.Reactive.Subjects
open System.Reactive.Linq
open MimeKit

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


let private receiveEmails (listener: TcpListener) (cancellation: CancellationToken) =
    async {
        let cancellationTask = TaskCompletionSource()

        use cancellationEvent =
            cancellation.Register(fun () -> cancellationTask.SetCanceled())

        let acceptClientTask = listener.AcceptTcpClientAsync()

        do!
            Task.WhenAny(
                [ cancellationTask.Task
                  acceptClientTask ]
            )
            |> Async.AwaitTask
            |> Async.Ignore

        if cancellationTask.Task.IsCanceled then
            return Result.Error "task is canceled"
        else
            use! client = acceptClientTask |> Async.AwaitTask
            use stream = client.GetStream()
            let rw = ReaderWriter.create stream
            let data = smtpHandler rw
            let messages = rw.getAll ()

            client.Close()

            return
                data
                |> Result.map (fun email -> (email, messages))
    }

type public ForwardServerConfig = { Port: int; Host: string }

let private forwardMessages forwardServer messages =
    task {
        use client = new TcpClient()

        do!
            client.ConnectAsync(forwardServer.Host, forwardServer.Port)

        use stream = client.GetStream()
        use streamWriter = new StreamWriter(stream)
        streamWriter.NewLine <- "\r\n"
        streamWriter.AutoFlush <- true
        use streamReader = new StreamReader(stream)

        for message in messages do
            match message with
            | Received m ->
                do!
                    streamWriter.WriteLineAsync(m)
            | Sent m ->
                let! x = streamReader.ReadLineAsync()
                ()

        client.Close()
    }

let private smtpActor (cachingActor: Actor<CheckInbox>) port forwardServer (cancellation: CancellationToken) =
    Actor.Start (fun _ ->
        let endPoint = new IPEndPoint(IPAddress.Any, port)
        let listener = new TcpListener(endPoint)
        listener.Start()

        let rec loop () =
            async {
                let! emailResult = receiveEmails listener cancellation

                match emailResult with
                | Result.Error _ -> listener.Stop()
                | Result.Ok (email, recorded) ->
                    email |> Add |> cachingActor.Post

                    match forwardServer with
                    | Some config -> do! forwardMessages config recorded |> Async.AwaitTask
                    | _ -> ()

                if cancellation.IsCancellationRequested then
                    listener.Stop()
                else
                    return! loop ()
            }

        loop ())

let private cachingActor emailReceived =
    Actor.Start (fun inbox ->
        let rec loop messages =
            async {
                let! newMessage = inbox.Receive()

                match newMessage with
                | Get channel ->
                    channel.Reply messages
                    return! loop messages
                | GetAndReset channel ->
                    channel.Reply messages
                    return! loop []
                | Add message ->
                    message |> emailReceived
                    return! loop (message :: messages)
            }

        loop [])

let public NoForwardServer = { Port = -1; Host = "" }


type public Server(port, thruServer) =
    let cancellationSource = new CancellationTokenSource()
    let emailReceivedEvent = new Subject<Email>()
    let cache = cachingActor emailReceivedEvent.OnNext

    let server =
        smtpActor
            cache
            port
            (if thruServer = NoForwardServer then
                 None
             else
                 Some thruServer)
            cancellationSource.Token

    new(port) = new Server(port, NoForwardServer)
    new() = new Server(25, NoForwardServer)

    interface IDisposable with
        member x.Dispose() =
            cancellationSource.Cancel(false)
            cancellationSource.Dispose()


    member this.GetEmails() = cache.PostAndReply Get
    member this.GetEmailsAndReset() = cache.PostAndReply GetAndReset
    member this.Port = port
    member this.EmailReceived = emailReceivedEvent.AsObservable()

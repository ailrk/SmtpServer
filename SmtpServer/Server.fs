module SMTP


open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open MimeKit
open System.Threading.Tasks
open System.Text.RegularExpressions


type private Agent<'T> = MailboxProcessor<'T>


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


    let private createAgent (sr: StreamReader, wr: StreamWriter) =
        Agent.Start (fun inbox ->
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
        let agent = createAgent <| (sr, wr)

        { read = fun () -> agent.PostAndReply Read
          write = fun line -> agent.Post(Write line)
          getAll = fun () -> agent.PostAndReply GetAll }


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


let smtp (rw: ReaderWriter.t) =
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

let emailsServer (port: int) (host: string) : Result<Email, string> seq =
    let addr = IPAddress.Parse host
    let server = new TcpListener(addr, port)

    try

        server.Start()
        let buffer = Array.create<byte> 256 0uy

        let rec loop () =
            seq {
                use client = server.AcceptTcpClient()
                printfn $"[connected]\n"
                let mutable stream = client.GetStream()
                let rw = ReaderWriter.create stream

                yield smtp rw
                client.Close()
                yield! loop ()
            }

        loop ()

    with
    | ex ->
        printfn $"[emailServer] exception{ex}"
        server.Stop()
        Seq.empty

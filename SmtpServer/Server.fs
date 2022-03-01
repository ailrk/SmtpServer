module SMTP


open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open MimeKit
open System.Threading.Tasks


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


let private (|DATA|QUIT|HELLO|EHLO|NOOP|) (input: string) =

    match input |> String.map Char.ToLower with
    | "data" -> DATA
    | "quit" -> QUIT
    | "hello" -> HELLO
    | "ehlo" -> EHLO
    | "noop" -> NOOP
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
            rw.write "250 OK, ciao"

            match email with
            | Some e -> Result.Ok e
            | None -> Result.Error "[smtp] error when readingDATA section"

        | HELLO ->
            rw.write "220 HELLO there"
            None |> readlines

        | EHLO ->
            rw.write "220 EHLO there"
            None |> readlines

        | rest ->
            rw.write "250 OK"
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
                let stream = client.GetStream()
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

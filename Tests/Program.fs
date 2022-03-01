module Program =
    open FSharp.Control.Reactive
    open System.Threading
    open SMTP

    let port = 8888

    [<EntryPoint>]
    let main _ =
        printf $"[starting mail serivce] port: {port}\n"

        let onNext (email: Email) = printf $"{email} received"
        let server = SMTP.Server.create port None

        server.emailReceived
        |> Observable.subscribe onNext
        |> ignore

        let rec loop () =
            Thread.Sleep 1000
            loop ()

        loop ()


        0

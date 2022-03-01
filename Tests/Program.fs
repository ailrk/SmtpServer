module Program =
    open FSharp.Control.Reactive
    open System.Threading
    open SMTP

    let port = 8888

    [<EntryPoint>]
    let main _ =
        printf $"[starting mail serivce] port: {port}\n"

        let onNext (mail: Email) =
            printfn
                $"{mail} received
            "

        let onError err =
            printf $"[email] an error happened: {err}"

        let onCompleted _ = ()

        let server = new SMTP.Server(port)

        server.EmailReceived
        |> Observable.subscribeWithCallbacks onNext onError onCompleted
        |> ignore

        let rec loop () =
            Thread.Sleep 1000
            loop ()
        loop ()

        0

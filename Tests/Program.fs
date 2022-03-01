module Program =
    open System.Threading
    open SMTP

    let port = 8888

    [<EntryPoint>]
    let main _ =
        printf $"[starting mail serivce] port: {port}\n"
        let emails = emailsServer 8888 "127.0.0.1"

        for n in emails do
            match n with
            | Result.Ok (Email mime) -> printfn $"mime {mime}"
            | Result.Error err -> printf $"err {err}"

        0

module Program =
    open FSharp.Control.Reactive
    open SMTP

    [<EntryPoint>]
    let main _ =
        printfn "[starting smtp server]"
        use server = new Server 8888
        0

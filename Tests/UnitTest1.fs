module Tests

open NUnit.Framework
open System.Threading
open System.Threading.Tasks
open SMTP
open System
open NUnit.Framework
open MimeKit
open MailKit.Net.Smtp


[<SetUp>]
let Setup () = ()

[<Literal>]
let TIMEOUT = 5000


let rand = new Random()

let getSut () =
    let port () = rand.Next(TIMEOUT, 9001)
    new Server(port ())


let getEmails (sut: Server) (getEmailsFunc: Server -> SMTP.EMail seq) sendEmails =
    task {
        use countdown =
            new CountdownEvent(Array.length sendEmails)

        use _ =
            sut.EmailReceived.Subscribe(fun actual -> countdown.Signal() |> ignore)

        Task.WaitAll <| Array.map Task.Run sendEmails
        return (getEmailsFunc sut)
    }

let sendEmailAsync (sut: Server) (msgs: MimeMessage array) =
    task {
        use client = new SmtpClient()
        let! _ = client.ConnectAsync("localhost", sut.Port, false)


        msgs
        |> Array.map (fun msg ->
            task {
                let! _ = client.SendAsync msg
                return ()
            })
        |> Seq.cast<Task>
        |> Array.ofSeq
        |> Task.WaitAll

        client.Disconnect true
    }


let assertEmailsAreEqual (actual: SMTP.EMail, expected: SMTP.EMail) =
    CollectionAssert.AreEqual(actual.From, expected.From)
    Assert.That(actual.To, Is.EqualTo(expected.To))
    Assert.That(actual.Subject, Is.EqualTo(expected.Subject))
    CollectionAssert.AreEquivalent(expected.Headers, actual.Headers)
    CollectionAssert.AreEqual(expected.Body, actual.Body)


type Message =
    { from: string
      t: string
      subject: string
      body: string }


let simple =
    { from = "x@from.com"
      t = "y@to.com"
      subject = "title"
      body = "body" }


let createSimpleMessage (m: Message) =
    use msg = new MimeMessage()
    msg.From.Add(new MailboxAddress("", m.from))
    msg.From.Add(new MailboxAddress("", m.t))
    msg.Subject <- m.subject
    msg.Body <- new TextPart("pain", Text = m.body)
    msg




[<Test>]
[<Timeout(TIMEOUT)>]
let ``should return empty list when no emails send`` () =
    use sut = getSut ()
    let actual = sut.GetEmails()
    Assert.That(actual, Is.Empty)



[<Test>]
[<Timeout(TIMEOUT)>]
let ``should return email when email is sent`` () =
    task {
        use sut = getSut ()
        let msg = createSimpleMessage simple

        let sendEmail =
            fun () -> sendEmailAsync sut [| msg |] :> Task

        let! emails = getEmails sut (fun s -> s.GetEmails()) [| sendEmail |]
        let actual = Seq.head emails
        ()
    }


[<Test>]
[<Timeout(TIMEOUT)>]
let ``should return multiple emails when sent`` () =
    task {

        use sut = getSut ()
        let msg = new MimeMessage()
        msg.From.Add(new MailboxAddress("", "from@a.com"))
        msg.To.Add(new MailboxAddress("", "to@b.com"))
        msg.Subject <- "subject"
        msg.Body <- new TextPart("plain", Text = "body")

        let msg2 = new MimeMessage()
        msg2.From.Add(new MailboxAddress("", "from2@a.com"))
        msg2.To.Add(new MailboxAddress("", "to2@b.com"))
        msg2.Subject <- "subject2"
        msg2.Body <- new TextPart("plain", Text = "body2")

        let sendEmail =
            fun () -> sendEmailAsync sut [| msg |] :> Task

        let! emails = getEmails sut (fun s -> s.GetEmails()) [| sendEmail |]

        ()
    }


[<Test>]
[<Timeout(TIMEOUT)>]
let ``should be able to reset email store`` () =
    task {
        use sut = getSut ()
        let msg = createSimpleMessage simple

        let sendEmail =
            fun () -> sendEmailAsync sut [| msg |] :> Task

        let! emails = getEmails sut (fun s -> s.GetEmails()) [| sendEmail; sendEmail |]
        Assert.That(Seq.length emails, Is.EqualTo 2)

        let! emails = getEmails sut (fun s -> s.GetEmails()) [| sendEmail |]
        Assert.That(Seq.length emails, Is.EqualTo 1)
    }

[<Test>]
[<Timeout(TIMEOUT)>]
let ``should return multiple emails when sent from same connection`` () =
    task {
        use sut = getSut ()
        let msg = new MimeMessage()
        msg.From.Add(new MailboxAddress("", "from@a.com"))
        msg.To.Add(new MailboxAddress("", "to@b.com"))
        msg.Subject <- "subject"
        msg.Body <- new TextPart("plain", Text = "body")

        let msg2 = new MimeMessage()
        msg2.From.Add(new MailboxAddress("", "from2@a.com"))
        msg2.To.Add(new MailboxAddress("", "to2@b.com"))
        msg2.Subject <- "subject2"
        msg2.Body <- new TextPart("plain", Text = "body2")

        let sendEmail =
            fun () -> sendEmailAsync sut [| msg |] :> Task

        let! emails = getEmails sut (fun s -> s.GetEmails()) [| sendEmail |]

        ()
    }

[<Test>]
[<Timeout(TIMEOUT)>]
let ``should return empty list when body is empty`` () =
    task {
        use sut = getSut ()

        let msg =
            createSimpleMessage { simple with body = String.Empty }

        let sendEmail =
            fun () -> sendEmailAsync sut [| msg |] :> Task

        let! emails = getEmails sut (fun s -> s.GetEmails()) [||]
        let actual = Seq.head emails
        Assert.That(Seq.length actual.Body, Is.EqualTo 9)
    }


[<Test>]
[<Timeout(TIMEOUT)>]
let ``should return empty string when subject is not provided`` () =
    task {
        use sut = getSut ()

        let msg =
            createSimpleMessage { simple with subject = null }

        let sendEmail =
            fun () -> sendEmailAsync sut [| msg |] :> Task

        let! emails = getEmails sut (fun s -> s.GetEmails()) [| sendEmail |]
        let actual = Seq.head emails
        Assert.That(actual.Subject, Is.EqualTo <| String.Empty)
    }


[<Test>]
[<Timeout(TIMEOUT)>]
let ``should return multiple from addresses`` () =
    task {
        use sut = getSut ()

        let msg =
            createSimpleMessage { simple with t = "to@b.com" }

        Assert.That(msg.To.Count, Is.EqualTo 2)

        let sendEmail =
            fun () -> sendEmailAsync sut [| msg |] :> Task

        let! emails = getEmails sut (fun s -> s.GetEmails()) [| sendEmail |]
        let actual = Seq.head emails
        ()
    }


[<TestCase("Subject")>]
[<TestCase("From")>]
[<TestCase("To")>]
[<TestCase("Body")>]
[<TestCase("Content-Type")>]
[<TestCase("MIME-Version")>]
[<TestCase("Priority")>]
[<TestCase("Date")>]
[<Timeout(TIMEOUT)>]
let ``should not overwrite headers from body`` (field: string) =
    task {
        use sut = getSut ()

        let msg =
            createSimpleMessage { simple with body = field + ": overwritten" }

        Assert.That(msg.To.Count, Is.EqualTo 2)

        let sendEmail =
            fun () -> sendEmailAsync sut [| msg |] :> Task

        let! emails = getEmails sut (fun s -> s.GetEmails()) [| sendEmail |]
        let actual = Seq.head emails
        ()
    }

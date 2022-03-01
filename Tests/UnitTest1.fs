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

module Logary.Targets.Logentries

open System
open System.IO
open System.Net.Sockets
open System.Text

open FSharp.Actor

open Logary
open Logary.Target
open Logary.Internals
open System.Net
open System.Security.Cryptography.X509Certificates
open System.Net.Security

/// Configuration for logentries
type LogentriesConf =
  { token        : string
    accountKey   : string
    useTls       : bool
    useHttp      : bool
    flush        : bool
    caValidation : X509Certificate -> X509Chain -> SslPolicyErrors -> bool
    formatter    : Formatting.StringFormatter }

/// Empty logentries configuration
let empty =
  { token        = ""
    accountKey   = ""
    flush        = false
    useHttp      = false
    useTls       = true
    caValidation = fun _ _ _ -> true
    // see https://logentries.com/doc/search/#syntax
    formatter    = Formatting.JsonFormatter.Default () }

/// Logentries internal implementations
module internal Impl =
  open System
  open System.Net.Security
  open Logary.Internals.Tcp

  type State = { client : TcpClient; stream :  Stream }

  [<Literal>]
  let LineSep = "\u2028"

  let Newlines = [ "\r\n"; "\n" ]

  let utf8 = Encoding.UTF8

  let munge (token : string) useHttp (msg : string) =
    let message = 
      String.concat "" [|
          (Newlines |> List.fold (fun (s : string) t -> s.Replace(t, LineSep)) msg)
        |]

    let protocol = 
      if useHttp then
        [|
            sprintf "POST /v1/logs/%s HTTP/1.1" token
            "Host: js.logentries.com"
            "Content-Type: application/json"
            "X-Requested-With: XMLHttpRequest"
            sprintf "Content-Length: %i\r\n" (message.Length)
         |]
      else Array.empty

    String.concat "\r\n" ([| message |] |> Array.append protocol) 

  let send (msg : byte []) (stream : Stream) flush = async {
    use ms = new MemoryStream(msg)
    do! transfer msg.Length ms stream
    if flush then
      stream.Flush()
    }

  type HostOrUri =
    | Host of string
    | Uri of Uri

  let gethostbyname uri useTls useHttp = 
//    let host, port = 
    match uri with 
    | Host host -> 
        let port = 
          match (useTls, useHttp) with
          | true, true -> 443
          | true, false -> 20000
          | false, true -> 80
          | false, false -> 10000
  
        (host, port)
    | Uri uri ->
        (uri.Host, uri.Port)

//    let! addresses = Dns.GetHostAddressesAsync(host) |> Async.AwaitTask
//    return new IPEndPoint(addresses.[0], port);
    

  let loop (conf : LogentriesConf) (ri : RuntimeInfo) (inbox : IActor<_>) =
    let rec initialise () = async {
      let proxy = WebRequest.GetSystemWebProxy()
      let uri = 
        if conf.useHttp then 
          let host = "js.logentries.com"
          let proxiedUri = proxy.GetProxy(new Uri(sprintf "http://%s" host))
          if proxiedUri.Host = host then 
            Host host
          else 
            Uri proxiedUri
        else Host "api.logentries.com"

      let host, port = gethostbyname uri conf.useTls conf.useHttp
      let client = new TcpClient(host, port)
      client.NoDelay <- true
      let stream =
        if conf.useTls then
          let validate = new RemoteCertificateValidationCallback(fun _ -> conf.caValidation)
          new SslStream(client.GetStream(), false, validate) :> Stream
        else
          client.GetStream() :> Stream

      stream.ReadTimeout <- 100
      
      return! running { client = client; stream = stream }
      }

     and running ({ client = client; stream = stream } as state) = async {
      let munge = munge conf.token conf.useHttp 
      let! msg, _ = inbox.Receive()
      match msg with
      | Log l ->
        // see https://logentries.com/doc/search/#syntax
        let msg = l |> conf.formatter.format |> munge |> utf8.GetBytes
        do! send msg stream conf.flush
        return! running state

      | Measure msr ->
        // doing key-value-pair style
        let msg =
          sprintf "%s=%f" msr.m_path.joined (msr |> Measure.getValueFloat)
          |> munge |> utf8.GetBytes
        do! send msg stream conf.flush
        return! running state

      | Flush ackChan ->
        ackChan.Reply Ack
        return! running state

      | Shutdown ackChan ->
        Try.safe "disposing Logentries clients" ri.logger <| fun () ->
          (stream :> IDisposable).Dispose()
          (client :> IDisposable).Dispose()
        ackChan.Reply Ack
        return ()
      }

    initialise ()

/// Create a new Logentries target
let create conf = TargetUtils.stdNamedTarget (Impl.loop conf)

/// C# Interop: Create a new Logentries target
[<CompiledName "Create">]
let create' (conf, name) =
  create conf name

/// Use with LogaryFactory.New( s => s.Target<Logentries.Builder>() )
type Builder(conf, callParent : FactoryApi.ParentCallback<Builder>) =
  /// Specify your API token
  member x.Token(token : string) =
    Builder({ conf with token = token }, callParent)

  /// Specify your account key
  member x.AccountKey(key : string) =
    Builder({ conf with accountKey = key }, callParent)

  member x.ForceFlush() =
    Builder({ conf with flush = true }, callParent)

  member x.Formatter(formatter : Formatting.StringFormatter) =
    Builder({ conf with formatter = formatter }, callParent)

  member x.Done () =
    ! (callParent x)

  new(callParent : FactoryApi.ParentCallback<_>) =
    Builder(empty, callParent)

  interface Logary.Target.FactoryApi.SpecificTargetConf with
    member x.Build name = create conf name

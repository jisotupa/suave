namespace Suave

module WebSocket =

  open Suave.Sockets
  open Suave.Sockets.Control
  open Suave.Operators
  open Suave.Utils
  open Suave.Logging
  open Suave.Logging.Message

  open System
  open System.Collections.Concurrent
  open System.Security.Cryptography
  open System.Text

  let magicGUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"

  let internal sha1 (x : string) =
#if NETSTANDARD1_5
    let crpto = SHA1.Create()
#else
    let crpto = new SHA1Managed()
#endif
    let bytes = Encoding.ASCII.GetBytes x
    let hash = crpto.ComputeHash bytes
    hash

  let handShakeResponse (handShakeToken : string) =
    Writers.setHeader "Upgrade" "websocket"
    >=> Writers.setHeader "Connection" "Upgrade"
    >=> Writers.setHeader "Sec-WebSocket-Accept" handShakeToken
    >=> Response.response HTTP_101 [||] // HTTP 1xx responses  MUST NOT contain a body

  type Opcode = Continuation | Text | Binary | Reserved | Close | Ping | Pong

  let toOpcode = function
    | 0uy -> Continuation
    | 1uy -> Text
    | 2uy -> Binary
    | 3uy | 4uy | 5uy | 6uy | 7uy -> Reserved
    | 8uy -> Close
    | 9uy -> Ping
    | 10uy -> Pong
    | 11uy | 12uy | 13uy | 14uy | 15uy -> Reserved
    | _ -> failwith "Invalid opcode."

  let fromOpcode = function
    | Continuation -> 0uy
    | Text -> 1uy
    | Binary -> 2uy
    | Close -> 8uy
    | Ping -> 9uy
    | Pong -> 10uy
    | _ -> failwith "Invalid opcode usage."

  type CloseCode =
    | CLOSE_NORMAL
    | CLOSE_GOING_AWAY
    | CLOSE_PROTOCO_ERROR
    | CLOSE_UNSUPPORTED
    | CLOSE_NO_STATUS
    | CLOSE_ABNORMAL
    | CLOSE_TOO_LARGE
    member x.code =
      match x with
      | CLOSE_NORMAL -> 1000
      | CLOSE_GOING_AWAY -> 1001
      | CLOSE_PROTOCO_ERROR -> 1002
      | CLOSE_UNSUPPORTED -> 1003
      | CLOSE_NO_STATUS -> 1005
      | CLOSE_ABNORMAL -> 1006
      | CLOSE_TOO_LARGE -> 1009

  type FrameHeader =
    { fin     : bool
      rsv1    : byte
      rsv2    : byte
      rsv3    : byte
      opcode  : Opcode
      hasMask : bool
      length  : int }

  type Frame = {
    OpcodeByte: byte
    EncodedLength: byte[]
    Data: ByteSegment
    }

  let internal exctractHeader (arr: byte []) =
    let bytes x = arr.[x]
    let firstByte = bytes 0
    let secondByte = bytes 1
    let length = int <| if secondByte >= 128uy then secondByte - 128uy else secondByte
    let header =
      { fin  = firstByte &&& 128uy <> 0uy
        rsv1 = firstByte &&& 64uy
        rsv2 = firstByte &&& 32uy
        rsv3 = firstByte &&& 16uy
        opcode = firstByte &&& 15uy |> toOpcode
        hasMask = secondByte &&& 128uy <> 0uy
        length = length
      }
    header

  let writeFrame (connection: Connection) (f: Frame) = socket {
      do! connection.transport.write (ArraySegment([| f.OpcodeByte |], 0, 1))
      do! connection.transport.write (ArraySegment(f.EncodedLength, 0, f.EncodedLength.Length))
      do! connection.transport.write f.Data
      }

  let internal frame (opcode : Opcode) (data : ByteSegment)  (fin : bool) =

    let firstByte = fromOpcode opcode

    let firstByte = if fin then firstByte ||| 128uy else firstByte

    let encodedLength = 
        match data.Count with
        | l when l >= 65536 ->
          [| yield 127uy
             yield! BitConverter.GetBytes(uint64 data.Count) |> Array.rev |]
        | l when l >= 126 ->
          [| yield 126uy
             yield! BitConverter.GetBytes(uint16 data.Count) |> Array.rev |]
        | _ -> [| byte (data.Count) |]
    
    { Frame.OpcodeByte = firstByte; EncodedLength = encodedLength; Data = data }

  let readBytes (transport : ITransport) (n : int) =
    let arr = Array.zeroCreate n
    let rec loop i = socket {
      if i = n then
        return arr
      else
        let! read = transport.read <| ArraySegment(arr,i,n - i)
        return! loop (i+read)
      }
    loop 0

  type internal Cont<'t> = 't -> unit

  open System.Threading

  let fork f = ThreadPool.QueueUserWorkItem(WaitCallback(f)) |> ignore

  /// This class represents a WebSocket connection, it provides an interface to read and write to a WebSocket.
  type WebSocket(connection : Connection) =
    
    let mutable writing = false
    let mutable reading = false

    let readExtendedLength header = socket {
      if header.length = 126 then
          let! bytes = readBytes connection.transport 2
          return uint64(bytes.[0]) * 256UL + uint64(bytes.[1])
        elif header.length = 127 then
          let! bytes = readBytes connection.transport 8
          return uint64(bytes.[0]) * 65536UL * 65536UL * 65536UL * 256UL +
          uint64(bytes.[1]) * 65536UL * 65536UL * 65536UL +
          uint64(bytes.[2]) * 65536UL * 65536UL * 256UL +
          uint64(bytes.[3]) * 65536UL * 65536UL +
          uint64(bytes.[4]) * 65536UL * 256UL +
          uint64(bytes.[5]) * 65536UL +
          uint64(bytes.[6]) * 256UL +
          uint64(bytes.[7])
        else
          return uint64(header.length)
      }

    let writeQueue = new ConcurrentQueue<Frame*Cont<Choice<unit,Error>>>()
    let readQueue  = new ConcurrentQueue<Cont<Choice<Opcode*byte[]*bool,Error>>>()

    let rec writeloop _ =
      async{
        try
          match writeQueue.TryDequeue() with
          | true, (frame, ok) ->
            let! res = writeFrame connection frame 
            fork (fun _ -> ok res)
            do! writeloop ()
          | false, _ ->
            lock writeQueue (fun _ -> writing <- false)
        with ex ->
          lock writeQueue (fun _ -> writing <- false)
      }

    let sendFrame opcode bs fin =
      let frame = frame opcode bs fin
      Async.FromContinuations <| fun (ok,ex,cn) ->
        writeQueue.Enqueue (frame,ok)
        lock(writeQueue) (fun _ -> 
          if not writing then
            writing <- true
            Async.Start <| writeloop ())

    let readFrame () = socket {
      assert (List.length connection.segments = 0)
      let! arr = readBytes connection.transport 2
      let header = exctractHeader arr
      let! extendedLength = readExtendedLength header

      assert(header.hasMask)
      let! mask = readBytes connection.transport 4

      if extendedLength > uint64 Int32.MaxValue then
        let reason = sprintf "Frame size of %d bytes exceeds maximum accepted frame size (2 GB)" extendedLength
        let data =
          [| yield! BitConverter.GetBytes (CloseCode.CLOSE_TOO_LARGE.code)
             yield! UTF8.bytes reason |]
        do! sendFrame Close (ArraySegment(data)) true
        return! SocketOp.abort (InputDataError reason)
      else
        let! frame = readBytes connection.transport (int extendedLength)
        // Messages from the client MUST be masked
        let data = if header.hasMask then frame |> Array.mapi (fun i x -> x ^^^ mask.[i % 4]) else frame
        return (header.opcode, data, header.fin)
      }

    let rec readloop _ =
      async{
        try
          match readQueue.TryDequeue() with
          | true, ok ->
            let! res = readFrame ()
            fork (fun _ -> ok res)
            do! readloop ()
          | false, _ ->
            lock readQueue (fun _ -> reading <- false)
        with ex ->
          lock readQueue (fun _ -> reading <- false)
      }

    member this.read () =
       Async.FromContinuations <| fun (ok,ex,cn) ->
        readQueue.Enqueue (ok)
        lock(readQueue) (fun _ -> 
          if not reading then
            reading <- true
            Async.Start <| readloop ())

    member this.send opcode bs fin = sendFrame opcode bs fin

  let internal handShakeAux webSocketKey continuation (ctx : HttpContext) =
    socket {
      let webSocketHash = sha1 <| webSocketKey + magicGUID
      let handShakeToken = Convert.ToBase64String webSocketHash
      let! something = HttpOutput.run (handShakeResponse handShakeToken) ctx
      let webSocket = new WebSocket(ctx.connection)
      do! continuation webSocket ctx
    }

  /// The handShake combinator captures a WebSocket and pass it to the provided `continuation`
  let handShake (continuation : WebSocket -> HttpContext -> SocketOp<unit>) (ctx : HttpContext) = async {
    let r = ctx.request
    if r.``method`` <> HttpMethod.GET then
      return! RequestErrors.METHOD_NOT_ALLOWED "Method not allowed" ctx
    elif r.header "upgrade"  |> Choice.map (fun s -> s.ToLower()) <> Choice1Of2 "websocket" then
      return! RequestErrors.BAD_REQUEST "Bad Request" ctx
    else
      match r.header "connection" with
      // rfc 6455 - Section 4.1.6 : The request MUST contain a |Connection| header field whose value
      // MUST include the "Upgrade" token.
      | Choice1Of2 str when String.contains "Upgrade" str ->
        match r.header "sec-websocket-key" with
        | Choice1Of2 webSocketKey ->
          let! a = handShakeAux webSocketKey continuation ctx
          match a with
          | Choice1Of2 _ ->
            do ()

          | Choice2Of2 err ->
            ctx.runtime.logger.info (
              eventX "WebSocket disconnected"
              >> setSingleName "Suave.Websocket.handShake"
              >> setFieldValue "error" err)

          return! Control.CLOSE ctx
        | _ ->
          return! RequestErrors.BAD_REQUEST "Missing 'sec-websocket-key' header" ctx
      | _ ->
        return! RequestErrors.BAD_REQUEST "Bad Request" ctx
    }

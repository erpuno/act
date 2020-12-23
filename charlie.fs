module CPS

open System
open System.Threading
open System.Collections.Concurrent
open FSharp

type CounterMsg =
   | Add of int32
   | GetAndReset of (int32 -> unit)

type 'a ISharedActor =
   abstract Post : msg:'a -> unit
   abstract PostAndReply : msgFactory:(('b -> unit) -> 'a) -> 'b

type 'a SharedMailbox() =
   let msgs = ConcurrentQueue()
   let mutable isStarted = false
   let mutable msgCount = 0
   let mutable react = Unchecked.defaultof<_>
   let mutable currentMessage = Unchecked.defaultof<_>

   let rec execute(isFirst) =

      let inline consumeAndLoop() =
         react currentMessage
         currentMessage <- Unchecked.defaultof<_>
         let newCount = Interlocked.Decrement &msgCount
         if newCount <> 0 then execute false

      if isFirst then consumeAndLoop()
      else
         let hasMessage = msgs.TryDequeue(&currentMessage)
         if hasMessage then consumeAndLoop()
         else
            Thread.SpinWait 20
            execute false

   member __.Receive(callback) =
      isStarted <- true
      react <- callback

   member __.Post msg =
      while not isStarted do Thread.SpinWait 20
      let newCount = Interlocked.Increment &msgCount
      if newCount = 1 then
         currentMessage <- msg
         // Might want to schedule this call on another thread.
         execute true
      else msgs.Enqueue msg

   member __.PostAndReply msgFactory =
      let value = ref Unchecked.defaultof<_>
      use onReply = new AutoResetEvent(false)
      let msg = msgFactory (fun x ->
         value := x
         onReply.Set() |> ignore
      )
      __.Post msg
      onReply.WaitOne() |> ignore
      !value


   interface 'a ISharedActor with
      member __.Post msg = __.Post msg
      member __.PostAndReply msgFactory = __.PostAndReply msgFactory

module SharedActor =
  let Start f =
      let mailbox = new SharedMailbox<_>()
      f mailbox
      mailbox :> _ ISharedActor

  let rec loop count (mailbox:SharedMailbox<_>) =
      mailbox.Receive(fun msg ->
         match msg with
         | Add n -> loop (count + n) mailbox
         | GetAndReset reply ->
           reply count
           loop 0 mailbox)

  let sharedActor = Start (fun mailbox -> loop 0 mailbox)

  let ret (_:unit) = 0
  let eval args =
      let mutable u = sharedActor
      for i in 0 .. 14000000 do
          u.Post <| Add i

  // 14M msgs/sec demo for F# .NET 5.0 target for 8700

  // $ time bin/Debug/net5.0/charlie
  // real    0m1.109s
  // user    0m1.063s
  // sys     0m0.047s

  [<EntryPoint>]
  let main args = args |> eval |> ret

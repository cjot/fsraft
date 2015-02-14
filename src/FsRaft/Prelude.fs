﻿namespace FsRaft 
open System


[<AutoOpen>]
module Logging =

    open Printf 

    type RaftLogEntry =
        | Debug of string
        | Info of string
        | Warn of string
        | Error of string * exn

    let inline debug category (evt: Event<RaftLogEntry>) format =
        let prefix = sprintf "%s :: " category
        ksprintf (fun s -> evt.Trigger (Debug (prefix + s))) format

    let inline info category (evt: Event<RaftLogEntry>) format =
        let prefix = sprintf "%s :: " category
        ksprintf (fun s -> evt.Trigger (Info (prefix + s))) format

    let inline warn category (evt: Event<RaftLogEntry>) format =
        let prefix = sprintf "%s :: "  category
        ksprintf (fun s -> evt.Trigger (Warn (prefix + s))) format

module RaftConstants = 

    [<Literal>]
    let heartbeat = 250
    #if DEBUG
    [<Literal>]
    let electionTimeoutFrom = 1000
    let electionTimeoutTo = 2000
    #else
    [<Literal>]
    let electionTimeoutFrom = 2000 
    let electionTimeoutTo = 5000
    #endif

[<AutoOpen>]
module Prelude =

    open System

    let medianInt (input : int list) = 
        let sorted = input |> List.sort
        let m1 = 
            let len = sorted.Length - 1
            len / 2
        sorted.[m1]

    let inline dispose (o: obj) =
        match o with
        | :? IDisposable as x ->
            x.Dispose ()
        | _ -> ()

[<RequireQualifiedAccess>]
module Observable =
    open System.Threading

    let awaitPause event timeOut =
        let are = ref (new AutoResetEvent false)
        use timer = new Timers.Timer (timeOut)
        timer.AutoReset <- false
        timer.Elapsed.Add (fun _ -> (!are).Set() |> ignore)
        use sub = Observable.subscribe(fun _ -> timer.Stop(); timer.Start()) event
        timer.Start()
        (!are).WaitOne()

[<RequireQualifiedAccess>]
module Option =

    let inline protect f =
        try Some (f())
        with | _ -> None

    let inline iterNone f =
        function
        | None -> f ()
        | _ -> ()

[<AutoOpen>]
module Guid =

    open System

    let inline guid () =
        Guid.NewGuid ()

    let inline short (guid: Guid) =
        (string guid).Substring(0, 8)

    let inline lower (s : string) = s.ToLower ()

[<RequireQualifiedAccess>]
module Map =

    /// returns a new map with the keys from m2 removed from m1
    let inline difference m1 m2 =
        Map.fold (fun s k _ -> Map.remove k s) m1 m2

    /// filters m1 by the keys that ar elso present in m2
    let inline intersect m1 m2 =
        Map.fold (fun s k v ->
            if Map.containsKey k m2 then
                Map.add k v s
            else s) Map.empty m1 

    let inline zipIntersect m1 m2 =
        Map.fold (fun s k v ->
            match Map.tryFind k m2 with
            | Some x -> Map.add k (v, x) s
            | None -> s) Map.empty m1

    /// merges two maps using f - if there are identical keys present the key in m1 will be used
    let inline mergeWith f m1 m2 = 
        Map.fold (fun s k v ->
            match Map.tryFind k s with
            | Some x -> Map.add k (f x v) s
            | None -> Map.add k v s) m1 m2 

    /// merges two maps - if there are identical keys present the key in m1 will be used
    let inline merge m1 m2 =
        mergeWith (fun x _ -> x) m1 m2

    /// updates exisiting values in m1 with values from matching keys in m2
    let inline update m1 m2 =
        Map.fold (fun s k v ->
            match Map.tryFind k s with
            | Some x -> Map.add k v s
            | None -> s) m1 m2

    let inline except key =
        Map.filter (fun k _ -> k <> key)

    /// concatenates a list of maps using the merge function. head first.
    let inline concat maps =
        List.fold merge Map.empty maps 

    /// returns a list of the keys in the map 
    let inline keys m =
        Map.foldBack (fun k _ s -> k :: s) m []

    /// returns a list of the values in the map 
    let inline values m =
        Map.foldBack (fun _ v s -> v :: s) m []

    let inline count (m : Map<'T,'T2>) = m.Count

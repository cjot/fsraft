﻿namespace FsRaft

open System
open System.IO

module Persistence =

    open FSharpx
    open Nessos.FsPickler

    let registry = CustomPicklerRegistry ("raft")
//    registry.SetTypeNameConverter (new DefaultTypeNameConverter(false))
//    let pickler = FsPickler(registry)
    let pickler = new FsPickler()

    [<AutoOpen>]
    module Term =

        type private TermProtocol =
            | Write of AsyncReplyChannel<unit> * (int64 * Guid option)
            | Exit of AsyncReplyChannel<unit>

        type TermContext (stream : Stream) =
            let mutable cache : (int64 * Guid option) = 0L, None
            let agent =
                MailboxProcessor.Start (fun inbox ->
                    let rec loop (state : Stream) = async {
                        let! msg = inbox.Receive ()
                        match msg with
                        | Write (rc, data) ->
                            state.Position <- 0L
                            pickler.Serialize (state, data)
                            state.Flush()
                            rc.Reply()
                            return! loop state
                        | Exit rc ->
                            dispose state
                            rc.Reply() }
                    loop stream)
            do
                stream.Position <- 0L
                let storedValue = Option.protect (fun () -> pickler.Deserialize<int64 * Guid option> stream)
                cache <- Option.getOrElse (0L, None) storedValue 
                agent.Error.Add raise // break all the things

            member __.Current 
                with get () = fst cache
                and set (t : int64) =
                    if t > fst cache then 
                        agent.PostAndReply (fun rc -> Write (rc, (t, None)))
                        cache <- t, None // race condition

            member __.VotedFor 
                with get() = snd cache
                and set votedFor =
                    if snd cache = None then
                        agent.PostAndReply (fun rc ->  Write (rc, (fst cache, votedFor)))
                        cache <- fst cache, votedFor

            member __.Error = agent.Error

            interface IDisposable with
                member __.Dispose () =
                    agent.PostAndReply(fun rc -> Exit rc)
                    dispose agent

    [<AutoOpen>]
    module Logs =

        type Record = int * int64 * obj  //index, term, type
            
        let createRecord index term x : Record =
            index, term, x

        type private LogProtocol =
            | Read of AsyncReplyChannel<Record> * int64
            | Write of AsyncReplyChannel<int64> * Record
            | Exit of AsyncReplyChannel<unit>

        type LogAgent = MailboxProcessor<LogProtocol>
//
//        type Snapshot<'TState> =
//            { Term : int64
//              Index : int
//              State : 'TState
//              File : string }
        type LogContext =
            { Index : Map<int, int64 * int64> //log index, term, offset
              Agent : LogAgent
              NextIndex : int }
            interface IDisposable with
                member x.Dispose () =
                    x.Agent.PostAndReply (fun rc -> Exit rc)
                    dispose x.Agent

        let private write s t = 
            pickler.Serialize(s, t)

        let private read s : Record = 
            pickler.Deserialize(s)

        let private truncateHigher index largest m =
            [index + 1 .. largest]
            |> List.fold (flip Map.remove) m

        let writeRecord ({ Agent = agent
                           NextIndex = nextIndex } as context) (r : Record) =
            let idx, term, _ = r
            if idx > nextIndex then 
                failwith "Attempt to write log entry that would leave a gap in the log - this should never happen"

            let pos = agent.PostAndReply ((fun rc -> Write (rc, r)), 2000)
            
            { context with 
                Index = 
                    Map.add idx (term, pos) context.Index
                    |> truncateHigher idx context.NextIndex // remove higher indexes as these should be considered stale
                NextIndex = idx + 1 }

        let private readRecord { Agent = agent } pos : Record =
            agent.PostAndReply((fun rc -> Read (rc, pos)), 2000)

        let private tryReadRecord context pos =
            Option.protect (fun () -> readRecord context pos)

        let private readIndex (s : Stream) =
            s.Position <- 0L
            let rec inner index last =
                let pos = s.Position
                match Option.protect (fun () -> read s) with
                | Some (idx, term, _) -> 
                    let trimmed = 
                        Map.add idx (term, pos) index 
                        |> truncateHigher idx last
                    inner trimmed idx
                | None -> index, last
            inner Map.empty<int, int64 * int64> 0

        let tryGet (context : LogContext) index =
            match Map.tryFind index context.Index with
            | Some (_, offset) ->
                tryReadRecord context offset 
            | None -> None

        type Query =
            | All
            | From of int
            | Range of int * int

        let query context =
            let read =
                Seq.sort
                >> Seq.map(fun (idx, (_, offs)) ->
                    readRecord context offs)
            let index = context.Index
            function
            | All -> 
                context.Index
                |> Map.toSeq
                |> read
            | Range (start, finish) ->
                let start = start + 1
                [start .. (min finish (context.NextIndex - 1))]
                |> Seq.map (fun x -> x, Map.find x index) 
                |> read
            | From start ->
                let start = max 1 start
                [start .. context.NextIndex - 1]
                |> Seq.map (fun x -> x, Map.find x index) 
                |> read

        let copyAndCompact (input:Stream) (output:Stream) =
            output.Position <- 0L
            let index, _ = readIndex input
            index
            |> Map.toSeq
            |> Seq.iter (fun (_, (_, o)) ->
                input.Position <- o
                let record = read input
                let pos = output.Seek (0L, SeekOrigin.End)
                write output record
                output.Flush())

        let makeContext stream =
            let index, last = readIndex stream
            let agent = MailboxProcessor.Start (fun inbox ->
                let rec loop (stream: Stream) = async {
                    let! msg = inbox.Receive ()
                    match msg with
                    | Write (rc, r) ->
                        let pos = stream.Seek (0L, SeekOrigin.End)
                        write stream r
                        stream.Flush()
                        rc.Reply pos
                        return! loop stream 
                    | Read (rc, pos) ->
                        stream.Position <- pos
                        rc.Reply (read stream)
                        return! loop stream 
                    | Exit rc ->
                        dispose stream
                        rc.Reply() }
                loop stream)

            agent.Error.Add raise

            { Index = index 
              Agent = agent 
              NextIndex = last + 1 } 
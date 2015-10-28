﻿namespace MBrace.Azure.Runtime

open System
open System.IO
open System.Threading
open System.Runtime.Serialization

open Microsoft.FSharp.Control

open MBrace.Core.Internals
open MBrace.Runtime
open MBrace.Runtime.Utils

open Amazon.SQS.Model

open MBrace.Aws.Runtime
open MBrace.Aws.Runtime.Utilities

type internal WorkItemLeaseTokenInfo =
    {
        MessageId     : string
        QueueUri      : string
        ReceiptHandle : string
        ProcessId     : string
        WorkItemId    : Guid
        BatchIndex    : int option
        TargetWorker  : string option
        BlobKey       : string
    }
    with
        override this.ToString() = sprintf "leaseinfo:%A" this.MessageId


type internal LeaseAction =
    | Complete
    | Abandon

/// Periodically renews lock for supplied work item, releases lock if specified as completed.
[<Sealed; AutoSerializable(false)>]
type internal WorkItemLeaseMonitor private 
        (clusterId : ClusterId,
         info      : WorkItemLeaseTokenInfo,
         logger    : ISystemLogger) =
    let sqsClient = clusterId.SQSAccount.SQSClient

    let deleteMsg () = async {
        let req = DeleteMessageRequest(
                    QueueUrl      = info.QueueUri,
                    ReceiptHandle = info.ReceiptHandle)
        do! sqsClient.DeleteMessageAsync(req)
            |> Async.AwaitTaskCorrect
            |> Async.Ignore
    }

    let rec renewLoop (inbox : MailboxProcessor<LeaseAction>) = async {
        let! action = inbox.TryReceive(timeout = 60)
        match action with
        | None ->
            let req = ChangeMessageVisibilityRequest(
                         QueueUrl      = info.QueueUri,
                         ReceiptHandle = info.ReceiptHandle)
            req.VisibilityTimeout <- 60 // hide message from other workers for another 1 min

            let! res = sqsClient.ChangeMessageVisibilityAsync(req)
                       |> Async.AwaitTaskCorrect
                       |> Async.Catch

            match res with
            | Choice1Of2 _ -> 
                logger.Logf LogLevel.Debug "%A : lock renewed" info
                return! renewLoop inbox
            // TODO: handle case of receipt handle no longer valid (someone else had received the msg)
            // as 'lock lost' case 
            | Choice2Of2 (:? ReceiptHandleIsInvalidException) ->
                logger.Logf LogLevel.Warning "%A : lock lost" info
            | Choice2Of2 exn -> 
                logger.LogError <| sprintf "%A : lock renew failed with %A" info exn
                return! renewLoop inbox

        | Some Complete ->
            do! deleteMsg()
            logger.Logf LogLevel.Info "%A : completed" info

        | Some Abandon ->
            do! deleteMsg()           
            logger.Logf LogLevel.Info "%A : abandoned" info
    }

    let cts = new CancellationTokenSource()
    let mbox = MailboxProcessor.Start(renewLoop, cts.Token)

    member __.CompleteWith(action) = mbox.Post action

    interface IDisposable with 
        member __.Dispose() = cts.Cancel()

    static member Start(id : ClusterId, info : WorkItemLeaseTokenInfo, logger : ISystemLogger) =
        new WorkItemLeaseMonitor(id, info, logger)

[<AutoOpen>]
module private DynamoDBHelper =
    open Amazon.DynamoDBv2
    open Amazon.DynamoDBv2.DocumentModel

    let inline private doIfNotNull f = function
        | Nullable(x) -> f x
        | _ -> ()

    // NOTE: implement a specific put rather than reinvent the object persistence layer as that's a lot
    // of work and at this point not enough payoff
    let putWorkItemRecord (account : AwsDynamoDBAccount) tableName (record : WorkItemRecord) =
        async {
            let table = Table.LoadTable(account.DynamoDBClient, tableName)
            let doc   = new Document()

            doc.["Id"] <- DynamoDBEntry.op_Implicit(record.Id)
            doc.["ProcessId"] <- DynamoDBEntry.op_Implicit(record.ProcessId)
            doc.["Affinity"]  <- DynamoDBEntry.op_Implicit(record.Affinity)
            doc.["Type"]      <- DynamoDBEntry.op_Implicit(record.Type)
            doc.["ETag"]      <- DynamoDBEntry.op_Implicit(record.ETag)
            doc.["CurrentWorker"]  <- DynamoDBEntry.op_Implicit(record.CurrentWorker)
            doc.["LastException"]  <- DynamoDBEntry.op_Implicit(record.LastException)

            record.Kind  |> doIfNotNull (fun x -> doc.["Kind"] <- DynamoDBEntry.op_Implicit x)
            record.Index |> doIfNotNull (fun x -> doc.["Index"] <- DynamoDBEntry.op_Implicit x)
            record.Size  |> doIfNotNull (fun x -> doc.["Size"] <- DynamoDBEntry.op_Implicit x)
            record.MaxIndex  |> doIfNotNull (fun x -> doc.["MaxIndex"] <- DynamoDBEntry.op_Implicit x)
            record.Status    |> doIfNotNull (fun x -> doc.["Status"] <- DynamoDBEntry.op_Implicit x)
            record.Completed |> doIfNotNull (fun x -> doc.["Completed"] <- DynamoDBEntry.op_Implicit x)
            record.FaultInfo |> doIfNotNull (fun x -> doc.["FaultInfo"] <- DynamoDBEntry.op_Implicit x)

            record.EnqueueTime |> doIfNotNull (fun x -> doc.["EnqueueTime"] <- DynamoDBEntry.op_Implicit x)
            record.DequeueTime |> doIfNotNull (fun x -> doc.["DequeueTime"] <- DynamoDBEntry.op_Implicit x)
            record.StartTime   |> doIfNotNull (fun x -> doc.["StartTime"] <- DynamoDBEntry.op_Implicit x)
            record.CompletionTime |> doIfNotNull (fun x -> doc.["CompletionTime"] <- DynamoDBEntry.op_Implicit x)
            record.RenewLockTime  |> doIfNotNull (fun x -> doc.["RenewLockTime"] <- DynamoDBEntry.op_Implicit x)
            record.DeliveryCount  |> doIfNotNull (fun x -> doc.["DeliveryCount"] <- DynamoDBEntry.op_Implicit x)

            do! table.UpdateItemAsync(doc)
                |> Async.AwaitTaskCorrect
                |> Async.Ignore
        }

/// Implements ICloudWorkItemLeaseToken
type internal WorkItemLeaseToken =
    {
        ClusterId       : ClusterId
        CompleteAction  : MarshaledAction<LeaseAction> // ensures that LeaseMonitor is serializable across AppDomains
        WorkItemType    : CloudWorkItemType
        WorkItemSize    : int64
        TypeName        : string
        FaultInfo       : CloudWorkItemFaultInfo
        LeaseInfo       : WorkItemLeaseTokenInfo
        ProcessInfo     : CloudProcessInfo
        TargetWorker    : string option
    }
with
    interface ICloudWorkItemLeaseToken with
        member this.DeclareCompleted() : Async<unit> = async {
            this.CompleteAction.Invoke Complete
            this.CompleteAction.Dispose() // disconnect marshaled object

            let record = new WorkItemRecord(this.LeaseInfo.ProcessId, fromGuid this.LeaseInfo.WorkItemId)
            record.Status         <- nullable(int WorkItemStatus.Completed)
            record.CompletionTime <- nullable(DateTime.UtcNow)
            record.Completed      <- nullable true
            record.ETag           <- "*" 

            do! putWorkItemRecord this.ClusterId.DynamoDBAccount this.ClusterId.RuntimeTable record
        }
        
        member this.DeclareFaulted(edi : ExceptionDispatchInfo) : Async<unit> = async {
            this.CompleteAction.Invoke Abandon
            this.CompleteAction.Dispose() // disconnect marshaled object

            let record = new WorkItemRecord(this.LeaseInfo.ProcessId, fromGuid this.LeaseInfo.WorkItemId)
            record.Status         <- nullable(int WorkItemStatus.Faulted)
            record.Completed      <- nullable false
            record.CompletionTime <- nullableDefault
            // there exists a remote possibility that fault exceptions might be of arbitrary size
            // should probably persist payload to blob as done with results
            record.LastException  <- ProcessConfiguration.JsonSerializer.PickleToString edi
            record.FaultInfo      <- nullable(int FaultInfo.FaultDeclaredByWorker)
            record.ETag           <- "*"

            do! putWorkItemRecord this.ClusterId.DynamoDBAccount this.ClusterId.RuntimeTable record
        }
        
        member this.FaultInfo : CloudWorkItemFaultInfo = this.FaultInfo
        
        member this.GetWorkItem() : Async<CloudWorkItem> = async { 
            let! payload = S3Persist.ReadPersistedClosure<MessagePayload>(this.ClusterId, this.LeaseInfo.BlobKey)
            match payload with
            | Single item -> return item
            | Batch items -> return items.[Option.get this.LeaseInfo.BatchIndex]
        }
        
        member this.Id : CloudWorkItemId = this.LeaseInfo.WorkItemId
        
        member this.WorkItemType : CloudWorkItemType = this.WorkItemType
        
        member this.Size : int64 = this.WorkItemSize
        
        member this.TargetWorker : IWorkerId option = 
            match this.TargetWorker with
            | None -> None
            | Some w -> Some(WorkerId(w) :> _)
        
        member this.Process : ICloudProcessEntry = 
            new CloudProcessEntry(this.ClusterId, this.LeaseInfo.ProcessId, this.ProcessInfo) :> _
        
        member this.Type : string = this.TypeName

    /// Creates a new WorkItemLeaseToken with supplied configuration parameters
    static member Create
            (clusterId : ClusterId, 
             info      : WorkItemLeaseTokenInfo, 
             monitor   : WorkItemLeaseMonitor, 
             faultInfo : CloudWorkItemFaultInfo) = 
        failwith "not implemented yet"
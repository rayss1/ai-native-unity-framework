using AiNative.Protocol.Backend.V1;
using AiNative.Server.Control;
using AiNative.Server.Rooms;
using Google.Protobuf;
using NUnit.Framework;
namespace AiNative.Server.Rooms.Tests;
public class BattleDurabilityTests
{
    [Test]
    public async Task ConfirmingOlderResultDoesNotReopenAdmissionAfterAnotherWriteFailed()
    {
        string directory = Path.Combine(Path.GetTempPath(), "durability-" + Guid.NewGuid());
        try
        {
            var outbox = new DurableResultOutbox(directory, 3, 100000);
            await outbox.InitializeAsync();
            var older = new MatchResult { MatchId = "older" };
            Assert.That(await outbox.StoreAsync(older), Is.True);
            var allocation = Allocation("newer");
            Assert.That(outbox.TryReserve(allocation), Is.True);
            var newer = new MatchResult { MatchId = "newer", RoomId = "newer", NodeId = "node", BootEpoch = "boot", Players = { new PlayerResult { PlayerId = "p1" }, new PlayerResult { PlayerId = "p2" } } };
            string obstruction = Path.Combine(directory, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("newer"))) + ".result.pending");
            Directory.CreateDirectory(obstruction);
            try { await outbox.StoreAsync(newer); Assert.Fail("The blocked file write must fail."); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            Assert.That(outbox.CanAdmit, Is.False);
            await outbox.ConfirmAsync(older.MatchId, DurableResultOutbox.Hash(older));
            Assert.That(outbox.Pending, Is.Empty);
            Assert.That(outbox.CanAdmit, Is.False);
            Directory.Delete(obstruction);
            Assert.That(await outbox.StoreAsync(newer), Is.True);
            Assert.That(outbox.CanAdmit, Is.True);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Test] public async Task FencedCoordinatorCannotReleaseCurrentRoom()
    {
        string dir=Path.Combine(Path.GetTempPath(),"durability-"+Guid.NewGuid());
        try {
            var outbox=new DurableResultOutbox(dir,8,100000);await outbox.InitializeAsync();
            await using var pool=new BattleWorkerPool("node","boot",1,8,8,_=>new Room());
            var service=new BattleControlService(pool,outbox,"");pool.Fence("old","",true);pool.Start();var allocation=Allocation("a","old");pool.Reserve(allocation);await pool.CreateAsync("a","a","boot",coordinatorEpoch:"old");pool.Fence("new","",true);
            var response=await service.HandleAsync(new(ServiceRole.Coordinator,"peer"),ServiceMethods.Release,new RoomRelease{RoomId="a",AllocationId="a",BootEpoch="boot",CoordinatorEpoch="old"}.ToByteArray());
            Assert.That(response.Success,Is.False);Assert.That(pool.Inventory().Rooms.Single().State,Is.EqualTo("Ready"));
        }finally{if(Directory.Exists(dir))Directory.Delete(dir,true);}
    }
    [Test] public async Task SimultaneousControlReservationsCannotExceedDurableCountBound()
    {
        string dir=Path.Combine(Path.GetTempPath(),"durability-"+Guid.NewGuid());
        try{var outbox=new DurableResultOutbox(dir,2,100000);await outbox.InitializeAsync();await using var pool=new BattleWorkerPool("node","boot",2,8,8,_=>new Room());var service=new BattleControlService(pool,outbox,"");pool.Fence("coord","",true);pool.Start();
        var replies=await Task.WhenAll(Enumerable.Range(0,20).Select(i=>Task.Run(async()=>await service.HandleAsync(new(ServiceRole.Coordinator,"peer"),ServiceMethods.Reserve,new RoomReservation{Allocation=Allocation("a"+i,"coord")}.ToByteArray()))));
        Assert.That(replies.Count(r=>r.Success),Is.EqualTo(2));Assert.That(outbox.ReservedCount,Is.EqualTo(2));Assert.That(pool.Inventory().Rooms.Count,Is.EqualTo(2));}
        finally{if(Directory.Exists(dir))Directory.Delete(dir,true);}
    }
    [Test] public async Task ExactWireByteReservationsCoverEveryInt32AndConvertOnlyAfterDurability()
    {
        string root=Path.Combine(Path.GetTempPath(),"durability-"+Guid.NewGuid());
        try{
            var allocation=Allocation("utf8-"+new string('界',30));allocation.PlayerIds.Clear();for(int i=0;i<8;i++)allocation.PlayerIds.Add("player"+i+new string('界',10));
            var sizing=new DurableResultOutbox(Path.Combine(root,"sizing"),10,100000);await sizing.InitializeAsync();Assert.That(sizing.TryReserve(allocation),Is.True);long bound=sizing.ReservedBytes;
            var outbox=new DurableResultOutbox(Path.Combine(root,"exact"),2,bound*2);await outbox.InitializeAsync();var second=allocation.Clone();second.MatchId+="2";second.RoomId+="2";second.AllocationId+="2";
            // The second allocation has three extra identity bytes, so reserve its exact size as well.
            Assert.That(sizing.TryReserve(second),Is.True);long secondBound=sizing.ReservedBytes-bound;
            outbox=new DurableResultOutbox(Path.Combine(root,"exact"),2,bound+secondBound);await outbox.InitializeAsync();Assert.That(outbox.TryReserve(allocation),Is.True);Assert.That(outbox.TryReserve(second),Is.True);Assert.That(outbox.TryReserve(Allocation("third")),Is.False);Assert.That(outbox.CanAdmit,Is.False);Assert.That(outbox.CanAdmitRoom(allocation.MatchId),Is.True);
            var result=new MatchResult{MatchId=allocation.MatchId,RoomId=allocation.RoomId,NodeId=allocation.NodeId,BootEpoch=allocation.BootEpoch,Completion="Finished"};result.Players.Add(allocation.PlayerIds.Select(p=>new PlayerResult{PlayerId=p,Won=true,Kills=int.MinValue}));Assert.That(result.CalculateSize(),Is.LessThanOrEqualTo(bound));
            Assert.That(await outbox.StoreAsync(result),Is.True);Assert.That(outbox.ReservedCount,Is.EqualTo(1));Assert.That(outbox.ReservedBytes,Is.EqualTo(secondBound));Assert.That(outbox.Pending.Count,Is.EqualTo(1));outbox.ReleaseReservation(second.MatchId);Assert.That(outbox.ReservedBytes,Is.Zero);
        }finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [Test] public async Task ReservationCannotBeStolenByUnrelatedStoreOrConflictingRoster()
    {
        string dir=Path.Combine(Path.GetTempPath(),"durability-"+Guid.NewGuid());try{var outbox=new DurableResultOutbox(dir,1,10000);await outbox.InitializeAsync();var a=Allocation("a");Assert.That(outbox.TryReserve(a),Is.True);Assert.That(await outbox.StoreAsync(new MatchResult{MatchId="other"}),Is.False);Assert.That(outbox.CanAdmitRoom("a"),Is.True);
        var conflict=a.Clone();conflict.PlayerIds[0]="changed";Assert.That(outbox.TryReserve(conflict),Is.False);Assert.ThrowsAsync<InvalidDataException>(async()=>await outbox.StoreAsync(new MatchResult{MatchId="a",RoomId="a",NodeId="node",BootEpoch="boot",Players={new PlayerResult{PlayerId="other"}}}));Assert.That(outbox.ReservedCount,Is.EqualTo(1));outbox.ReleaseReservation("a");Assert.That(outbox.TryReserve(Allocation("b")),Is.True);}
        finally{if(Directory.Exists(dir))Directory.Delete(dir,true);}
    }
    [Test] public async Task CreationFailureAndWorkerFaultReclaimAdvanceReservationsOffTick()
    {
        string dir=Path.Combine(Path.GetTempPath(),"durability-"+Guid.NewGuid());try{var outbox=new DurableResultOutbox(dir,1,10000);await outbox.InitializeAsync();await using(var failed=new BattleWorkerPool("node","boot",1,1,8,_=>throw new InvalidOperationException("factory"))){failed.ConfigureResultOutbox(outbox);failed.Start();Assert.That(failed.Reserve(Allocation("a")),Is.Empty);Assert.ThrowsAsync<InvalidOperationException>(async()=>await failed.CreateAsync("a","a","boot"));Assert.That(outbox.ReservedCount,Is.Zero);Assert.That(failed.Inventory().Rooms,Is.Empty);}
        await using(var fault=new BattleWorkerPool("node","boot",1,1,8,_=>new FaultRoom())){fault.ConfigureResultOutbox(outbox);fault.Start();Assert.That(fault.Reserve(Allocation("b")),Is.Empty);await fault.CreateAsync("b","b","boot");for(int i=0;i<100&&outbox.ReservedCount!=0;i++){fault.Inventory();await Task.Delay(5);}Assert.That(outbox.ReservedCount,Is.Zero);Assert.That(fault.Inventory().Rooms.Single().State,Is.EqualTo("Lost"));Assert.That(await fault.ReleaseAsync("b","b","boot"),Is.Empty);Assert.That(fault.Inventory().Rooms,Is.Empty);}}
        finally{if(Directory.Exists(dir))Directory.Delete(dir,true);}
    }
    [Test] public async Task CancelBeforeInstallationCannotResurrectRoomAndReturnsReservation()
    {
        string dir=Path.Combine(Path.GetTempPath(),"durability-"+Guid.NewGuid());using var entered=new ManualResetEventSlim();using var unblock=new ManualResetEventSlim();try{var outbox=new DurableResultOutbox(dir,1,10000);await outbox.InitializeAsync();await using var pool=new BattleWorkerPool("node","boot",1,1,8,_=>new Room());pool.ConfigureResultOutbox(outbox);pool.Start();pool.TryPost(0,()=>{entered.Set();unblock.Wait();});Assert.That(entered.Wait(2000),Is.True);Assert.That(pool.Reserve(Allocation("a")),Is.Empty);using var cancel=new CancellationTokenSource();var create=pool.CreateAsync("a","a","boot",cancel.Token).AsTask();cancel.Cancel();Assert.ThrowsAsync<TaskCanceledException>(async()=>await create);Assert.That(outbox.ReservedCount,Is.Zero);unblock.Set();await Task.Delay(40);Assert.That(pool.Inventory().Rooms,Is.Empty);}
        finally{unblock.Set();if(Directory.Exists(dir))Directory.Delete(dir,true);}
    }
    [Test] public async Task ReleaseQueuedBeforeFenceChecksGenerationAgainOnWorkerExecution()
    {
        string dir=Path.Combine(Path.GetTempPath(),"durability-"+Guid.NewGuid());using var entered=new ManualResetEventSlim();using var unblock=new ManualResetEventSlim();try{var outbox=new DurableResultOutbox(dir,1,10000);await outbox.InitializeAsync();await using var pool=new BattleWorkerPool("node","boot",1,1,8,_=>new Room());pool.ConfigureResultOutbox(outbox);pool.Fence("old","",true);pool.Start();pool.Reserve(Allocation("a","old"));await pool.CreateAsync("a","a","boot",coordinatorEpoch:"old");pool.TryPost(0,()=>{entered.Set();unblock.Wait();});Assert.That(entered.Wait(2000),Is.True);var release=pool.ReleaseAsync("a","a","boot",coordinatorEpoch:"old").AsTask();pool.Fence("new","",true);unblock.Set();Assert.That(await release,Is.EqualTo("stale-coordinator"));Assert.That(outbox.ReservedCount,Is.EqualTo(1));Assert.That(pool.Inventory().Rooms.Single().State,Is.EqualTo("Ready"));Assert.Throws<InvalidOperationException>(()=>pool.Fence("old","",true));Assert.That(await pool.ReleaseAsync("a","a","boot",coordinatorEpoch:"new"),Is.Empty);Assert.That(outbox.ReservedCount,Is.Zero);}
        finally{unblock.Set();if(Directory.Exists(dir))Directory.Delete(dir,true);}
    }
    sealed class FaultRoom:IWorkerRoom{public void Tick()=>throw new InvalidOperationException("fault");public void Dispose(){}}
    [Test] public async Task RecoveryRejectsConfiguredFileCountOrByteOverflowBeforeLoading()
    {
        string root=Path.Combine(Path.GetTempPath(),"durability-"+Guid.NewGuid());try{var writer=new DurableResultOutbox(root,2,10000);await writer.InitializeAsync();await writer.StoreAsync(new MatchResult{MatchId="a"});await writer.StoreAsync(new MatchResult{MatchId="b"});var countLimited=new DurableResultOutbox(root,1,10000);var countError=Assert.ThrowsAsync<InvalidDataException>(async()=>await countLimited.InitializeAsync());Assert.That(countError!.Message,Is.EqualTo("outbox-recovery-capacity-exceeded"));Assert.That(countLimited.CanAdmit,Is.False);Assert.That(countLimited.Pending.Count,Is.LessThanOrEqualTo(1));
        var byteLimited=new DurableResultOutbox(root,2,1);var byteError=Assert.ThrowsAsync<InvalidDataException>(async()=>await byteLimited.InitializeAsync());Assert.That(byteError!.Message,Is.EqualTo("outbox-recovery-capacity-exceeded"));Assert.That(byteLimited.Pending,Is.Empty);Assert.That(byteLimited.CanAdmit,Is.False);}
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [Test] public async Task DrainRejectsRetiredCoordinatorAndAcceptsOnlyCurrentGeneration()
    {
        string dir=Path.Combine(Path.GetTempPath(),"durability-"+Guid.NewGuid());try{var outbox=new DurableResultOutbox(dir,1,10000);await outbox.InitializeAsync();await using var pool=new BattleWorkerPool("node","boot",1,1,8,_=>new Room());var service=new BattleControlService(pool,outbox,"");pool.Fence("old","",true);pool.Fence("new","",true);
        var old=await service.HandleAsync(new(ServiceRole.Coordinator,"peer"),ServiceMethods.Drain,new DrainRequest{BootEpoch="boot",CoordinatorEpoch="old"}.ToByteArray());Assert.That(old.Success,Is.False);Assert.That(pool.Inventory().Draining,Is.False);var current=await service.HandleAsync(new(ServiceRole.Coordinator,"peer"),ServiceMethods.Drain,new DrainRequest{BootEpoch="boot",CoordinatorEpoch="new"}.ToByteArray());Assert.That(current.Success,Is.True);Assert.That(pool.Inventory().Draining,Is.True);}
        finally{if(Directory.Exists(dir))Directory.Delete(dir,true);}
    }
    static RoomAllocation Allocation(string id,string coordinator="")=>new(){AllocationId=id,RoomId=id,MatchId=id,NodeId="node",BootEpoch="boot",CoordinatorEpoch=coordinator,PlayerIds={"p1","p2"}};
    sealed class Room:IWorkerRoom{public void Tick(){}public void Dispose(){}}
}

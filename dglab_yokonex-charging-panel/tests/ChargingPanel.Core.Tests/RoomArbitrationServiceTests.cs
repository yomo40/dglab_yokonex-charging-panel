using System;
using ChargingPanel.Core.Services;
using Xunit;

namespace ChargingPanel.Core.Tests;

public class RoomArbitrationServiceTests
{
    [Fact]
    public void Evaluate_ControlRequest_ShouldAcquireLease()
    {
        var service = new RoomArbitrationService();

        var decision = service.Evaluate(new RoomArbitrationRequest(
            SenderId: "controller_a",
            TargetUserId: "target_1",
            Action: "control_request",
            CommandId: Guid.NewGuid().ToString("N"),
            Priority: 1,
            LeaseTtl: TimeSpan.FromSeconds(20)));

        Assert.True(decision.Allowed);
        Assert.Equal("controller_a", service.GetControlOwner("target_1"));
    }

    [Fact]
    public void Evaluate_DuplicateCommand_ShouldBeRejected()
    {
        var service = new RoomArbitrationService();
        const string commandId = "cmd_duplicate_001";

        var first = service.Evaluate(new RoomArbitrationRequest(
            SenderId: "controller_a",
            TargetUserId: "target_1",
            Action: "set_strength",
            CommandId: commandId));
        var second = service.Evaluate(new RoomArbitrationRequest(
            SenderId: "controller_a",
            TargetUserId: "target_1",
            Action: "set_strength",
            CommandId: commandId));

        Assert.True(first.Allowed);
        Assert.False(second.Allowed);
        Assert.Equal(RoomArbitrationReason.DuplicateCommand, second.Reason);
    }

    [Fact]
    public void Evaluate_LowerPriority_ShouldNotTakeover()
    {
        var service = new RoomArbitrationService();

        var first = service.Evaluate(new RoomArbitrationRequest(
            SenderId: "controller_a",
            TargetUserId: "target_1",
            Action: "control_request",
            CommandId: "cmd_a",
            Priority: 10,
            LeaseTtl: TimeSpan.FromSeconds(20)));

        var second = service.Evaluate(new RoomArbitrationRequest(
            SenderId: "controller_b",
            TargetUserId: "target_1",
            Action: "set_strength",
            CommandId: "cmd_b",
            Priority: 1,
            LeaseTtl: TimeSpan.FromSeconds(20)));

        Assert.True(first.Allowed);
        Assert.False(second.Allowed);
        Assert.Equal(RoomArbitrationReason.LeaseOccupied, second.Reason);
        Assert.Equal("controller_a", service.GetControlOwner("target_1"));
    }

    [Fact]
    public void Evaluate_HigherPriority_ShouldTakeover()
    {
        var service = new RoomArbitrationService();

        service.Evaluate(new RoomArbitrationRequest(
            SenderId: "controller_a",
            TargetUserId: "target_1",
            Action: "control_request",
            CommandId: "cmd_a",
            Priority: 1,
            LeaseTtl: TimeSpan.FromSeconds(20)));

        var second = service.Evaluate(new RoomArbitrationRequest(
            SenderId: "controller_b",
            TargetUserId: "target_1",
            Action: "set_strength",
            CommandId: "cmd_b",
            Priority: 5,
            LeaseTtl: TimeSpan.FromSeconds(20)));

        Assert.True(second.Allowed);
        Assert.Equal("controller_b", service.GetControlOwner("target_1"));
    }

    [Fact]
    public void Evaluate_ControlRelease_ShouldReleaseLease()
    {
        var service = new RoomArbitrationService();

        service.Evaluate(new RoomArbitrationRequest(
            SenderId: "controller_a",
            TargetUserId: "target_1",
            Action: "control_request",
            CommandId: "cmd_a",
            Priority: 1,
            LeaseTtl: TimeSpan.FromSeconds(20)));

        var release = service.Evaluate(new RoomArbitrationRequest(
            SenderId: "controller_a",
            TargetUserId: "target_1",
            Action: "control_release",
            CommandId: "cmd_release"));

        var next = service.Evaluate(new RoomArbitrationRequest(
            SenderId: "controller_b",
            TargetUserId: "target_1",
            Action: "set_strength",
            CommandId: "cmd_next",
            Priority: 1));

        Assert.True(release.Allowed);
        Assert.True(next.Allowed);
        Assert.Equal("controller_b", service.GetControlOwner("target_1"));
    }
}


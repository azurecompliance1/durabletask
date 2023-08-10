// This file is used by Code Analysis to maintain SuppressMessage
// attributes that are applied to this project.
// Project-level suppressions either have no target or are given
// a specific target and scoped to a namespace, type, member, etc.

using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("Usage", "VSTHRD103:Call async methods when in an async method", Justification = "<Pending>", Scope = "member", Target = "~M:DurableTask.ServiceBus.Common.Abstraction.IMessageSession.GetStateAsync~System.Threading.Tasks.Task{System.Byte[]}")]
[assembly: SuppressMessage("Usage", "VSTHRD103:Call async methods when in an async method", Justification = "<Pending>", Scope = "member", Target = "~M:DurableTask.ServiceBus.Common.Abstraction.IMessageSession.SetStateAsync(System.Byte[])~System.Threading.Tasks.Task")]
[assembly: SuppressMessage("Usage", "VSTHRD111:Use ConfigureAwait(bool)", Justification = "<Pending>", Scope = "member", Target = "~M:DurableTask.ServiceBus.ServiceBusOrchestrationService.StartAsync~System.Threading.Tasks.Task")]
[assembly: SuppressMessage("Usage", "VSTHRD105:Avoid method overloads that assume TaskScheduler.Current", Justification = "<Pending>", Scope = "member", Target = "~M:DurableTask.ServiceBus.Tracking.JumpStartManager.StartAsync~System.Threading.Tasks.Task")]
[assembly: SuppressMessage("Usage", "VSTHRD105:Avoid method overloads that assume TaskScheduler.Current", Justification = "<Pending>", Scope = "member", Target = "~M:DurableTask.ServiceBus.ServiceBusOrchestrationService.StartAsync~System.Threading.Tasks.Task")]

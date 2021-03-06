﻿//-----------------------------------------------------------------------
// <copyright file="ClusterSingletonManagerChaosSpec2.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2016 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2016 Akka.NET project <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Linq;
using Akka.Actor;
using Akka.Cluster.Tests.MultiNode;
using Akka.Cluster.Tools.Singleton;
using Akka.Configuration;
using Akka.Remote.TestKit;
using Akka.TestKit;
using Akka.TestKit.Internal;
using Akka.TestKit.TestEvent;
using FluentAssertions;
using Xunit;

namespace Akka.Cluster.Tools.Tests.MultiNode.Singleton
{
    public class ClusterSingletonManagerChaos2Config : MultiNodeConfig
    {
        public readonly RoleName Controller;
        public readonly RoleName First;
        public readonly RoleName Second;
        public readonly RoleName Third;
        public readonly RoleName Fourth;

        public ClusterSingletonManagerChaos2Config()
        {
            Controller = Role("controller");
            First = Role("_config.First");
            Second = Role("second");
            Third = Role("third");
            Fourth = Role("fourth");

            CommonConfig = ConfigurationFactory.ParseString(@"
                akka.loglevel = DEBUG
                akka.actor.provider = ""Akka.Cluster.ClusterActorRefProvider, Akka.Cluster""
                akka.remote.log-remote-lifecycle-events = off
                akka.cluster.auto-down-unreachable-after = 0s
            ")
            .WithFallback(ClusterSingletonManager.DefaultConfig())
            .WithFallback(ClusterSingletonProxy.DefaultConfig())
            .WithFallback(MultiNodeClusterSpec.ClusterConfig());
        }
    }

    public class ClusterSingletonManagerChaos2Node1 : ClusterSingletonManagerChaos2Spec { }
    public class ClusterSingletonManagerChaos2Node2 : ClusterSingletonManagerChaos2Spec { }
    public class ClusterSingletonManagerChaos2Node3 : ClusterSingletonManagerChaos2Spec { }
    public class ClusterSingletonManagerChaos2Node4 : ClusterSingletonManagerChaos2Spec { }
    public class ClusterSingletonManagerChaos2Node5 : ClusterSingletonManagerChaos2Spec { }

    public abstract class ClusterSingletonManagerChaos2Spec : MultiNodeClusterSpec
    {
        private class EchoStarted
        {
            public static readonly EchoStarted Instance = new EchoStarted();

            private EchoStarted() { }
        }

        private class EchoActor : ReceiveActor
        {
            public EchoActor(IActorRef testActor)
            {
                testActor.Tell(EchoStarted.Instance);
                ReceiveAny(_ => Sender.Tell(Self));
            }
        }

        private readonly ClusterSingletonManagerChaos2Config _config;

        protected ClusterSingletonManagerChaos2Spec() : this(new ClusterSingletonManagerChaos2Config())
        {
        }

        protected ClusterSingletonManagerChaos2Spec(ClusterSingletonManagerChaos2Config config) : base(config)
        {
            _config = config;
        }

        protected override int InitialParticipantsValueFactory { get { return Roles.Count; } }

        [MultiNodeFact]
        public void ClusterSingletonManager_in_chaotic_cluster_should_take_over_when_tree_oldest_nodes_crash_in_6_nodes_cluster()
        {
            Within(TimeSpan.FromSeconds(90), () =>
            {
                var memberProbe = CreateTestProbe();
                Cluster.Subscribe(memberProbe.Ref, new[] { typeof(ClusterEvent.MemberUp) });
                memberProbe.ExpectMsg<ClusterEvent.CurrentClusterState>();

                Join(_config.First, _config.First);
                AwaitMemberUp(memberProbe, _config.First);

                Join(_config.Second, _config.First);
                AwaitMemberUp(memberProbe, _config.Second, _config.First);

                Join(_config.Third, _config.First);
                AwaitMemberUp(memberProbe, _config.Third, _config.Second, _config.First);

                Join(_config.Fourth, _config.First);
                AwaitMemberUp(memberProbe, _config.Fourth, _config.Third, _config.Second, _config.First);

                // mute logging of deadLetters during shutdown of systems
                if (!Log.IsDebugEnabled)
                    Sys.EventStream.Publish(new Mute(new WarningFilter()));
                EnterBarrier("logs-muted");

                Crash(_config.First, _config.Second, _config.Third);
                EnterBarrier("after-crash");
                RunOn(() =>
                {
                    ExpectMsg<EchoStarted>();
                }, _config.Fourth);
                EnterBarrier("_config.Fourth-active");

                RunOn(() =>
                {
                    Echo(_config.Fourth).Tell("hello");
                    var address = ExpectMsg<IActorRef>(TimeSpan.FromSeconds(3)).Path.Address;
                    Assert.Equal(address, GetAddress(_config.Fourth));
                }, _config.Controller);
                EnterBarrier("_config.Fourth-verified");
            });
        }

        private void Join(RoleName from, RoleName to)
        {
            RunOn(() =>
            {
                Cluster.Join(GetAddress(to));
                CreateSingleton();
            }, from);
        }

        private IActorRef CreateSingleton()
        {
            return Sys.ActorOf(ClusterSingletonManager.Props(
                singletonProps: Props.Create(() => new EchoActor(TestActor)),
                terminationMessage: PoisonPill.Instance,
                settings: ClusterSingletonManagerSettings.Create(Sys)),
                name: "echo");
        }

        private void Crash(params RoleName[] roles)
        {
            RunOn(() =>
            {
                foreach (var roleName in roles)
                {
                    Log.Info("Shutdown [{0}]", GetAddress(roleName));
                    TestConductor.Exit(roleName, 0).Wait(TimeSpan.FromSeconds(10));
                }
            }, _config.Controller);
        }

        private ActorSelection Echo(RoleName oldest)
        {
            return Sys.ActorSelection(new RootActorPath(GetAddress(oldest)) / "user" / "echo" / "singleton");
        }

        private void AwaitMemberUp(TestProbe memberProbe, params RoleName[] nodes)
        {
            if (nodes.Length > 1)
            {
                RunOn(() =>
                {
                    memberProbe.ExpectMsg<ClusterEvent.MemberUp>(TimeSpan.FromSeconds(15)).Member.Address
                        .Should()
                        .Be(GetAddress(nodes.First()));
                }, nodes.Skip(1).ToArray());
            }

            RunOn(() =>
            {
                var roleNodes = nodes.Select(node => GetAddress(node));

                var addresses = memberProbe.ReceiveN(nodes.Length, TimeSpan.FromSeconds(15))
                    .Where(x => x is ClusterEvent.MemberUp)
                    .Select(x => (x as ClusterEvent.MemberUp).Member.Address);

                addresses.Except(roleNodes).Count().Should().Be(0);
            }, nodes.First());

            EnterBarrier(nodes.First().Name + "-up");
        }
    }
}
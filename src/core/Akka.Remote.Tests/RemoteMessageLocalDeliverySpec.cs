﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Configuration;
using Akka.Remote.Transport;
using Akka.TestKit;
using Akka.TestKit.TestActors;
using Xunit;

namespace Akka.Remote.Tests
{
    /// <summary>
    /// Came across some issues while debugging multi-node tests which indicated
    /// that the <see cref="RemoteActorRefProvider"/> couldn't successfully decode full addresses
    /// for local actors, such as "akka.trttl.gremlin.tcp://AttemptSysMsgRedeliverySpec@localhost:57512/user/echo",
    /// into valid <see cref="LocalActorRef"/>s.
    /// 
    /// This spec is designed to verify that these types of paths, including ones with transport adapters at the front, 
    /// can be successfully translated.
    /// </summary>
    public class RemoteMessageLocalDeliverySpec : AkkaSpec
    {
        private static readonly Config RemoteConfiguration = ConfigurationFactory.ParseString(@"
            akka.actor.provider = ""Akka.Remote.RemoteActorRefProvider, Akka.Remote""
                akka.remote.helios.tcp.hostname = 127.0.0.1
                akka.remote.helios.tcp.port = 0
            akka.remote.helios.tcp.applied-adapters = [trttl, gremlin]
        ");

        public RemoteMessageLocalDeliverySpec() : base(RemoteConfiguration) { }

        [Fact]
        public void RemoteActorRefProvider_default_address_must_include_adapter_schemes()
        {
            var localAddress = RARP.For(Sys).Provider.DefaultAddress;
            Assert.True(localAddress.ToString().StartsWith("akka.trttl.gremlin.tcp://"));
        }

        [Fact]
        public void RemoteActorRefProvider_should_correctly_resolve_valid_LocalActorRef_from_remote_address()
        {
            var actorRef = Sys.ActorOf(BlackHoleActor.Props, "myActor");
            var localAddress = RARP.For(Sys).Provider.DefaultAddress;
            var actorPath = new RootActorPath(localAddress) / "user"  / "myActor";

            var resolvedActorRef = RARP.For(Sys).Provider.ResolveActorRefWithLocalAddress(actorPath.ToStringWithAddress(), localAddress);
            Assert.Equal(actorRef, resolvedActorRef);
        }

        [Fact]
        public void RemoteActorRefProvider_should_correctly_resolve_valid_LocalActorRef_from_second_remote_system()
        {
           var sys2 = ActorSystem.Create("Sys2", RemoteConfiguration);
            try
            {
                Within(TimeSpan.FromSeconds(15), () =>
                {
                    var actorRef = sys2.ActorOf(BlackHoleActor.Props, "myActor");
                    var sys2Address = RARP.For(sys2).Provider.DefaultAddress;
                    var actorPath = new RootActorPath(sys2Address) / "user" / "myActor";

                    // get a remoteactorref for the second system
                    var remoteActorRef = Sys.ActorSelection(actorPath).ResolveOne(TimeSpan.FromSeconds(3)).Result;

                    // disconnect us from the second actorsystem
                    var mc =
                        RARP.For(Sys)
                            .Provider.Transport.ManagementCommand(new SetThrottle(sys2Address,
                                ThrottleTransportAdapter.Direction.Both, Blackhole.Instance));
                    Assert.True(mc.Wait(TimeSpan.FromSeconds(3)));

                    // start deathwatch (won't be delivered initially)
                    Watch(remoteActorRef);
                    Task.Delay(TimeSpan.FromSeconds(3)).Wait(); // if we delay the initial send, this spec will fail

                    var mc2 =
                       RARP.For(Sys)
                           .Provider.Transport.ManagementCommand(new SetThrottle(sys2Address,
                               ThrottleTransportAdapter.Direction.Both, Unthrottled.Instance));
                    Assert.True(mc2.Wait(TimeSpan.FromSeconds(3)));

                    // fire off another non-system message
                    var ai =
                        Sys.ActorSelection(actorPath).Ask<ActorIdentity>(new Identify(null), TimeSpan.FromSeconds(3)).Result;

                   
                    remoteActorRef.Tell(PoisonPill.Instance); // WATCH should be applied first
                    ExpectTerminated(remoteActorRef);
                });
            }
            finally
            {
                Assert.True(sys2.Terminate().Wait(TimeSpan.FromSeconds(5)));
            }
        }
    }
}

using System.Collections.Generic;
using System.Linq;
using Hkmp.Api.Client;
using Hkmp.Api.Client.Networking;
using Hkmp.Api.Server;
using Hkmp.Api.Server.Networking;
using Hkmp.Networking.Packet;
using Hkmp.Networking.Packet.Data;
using UnityEngine;

namespace Stumble
{
    // Stumble's add-on for HKMP (Hollow Knight Multiplayer): helping each
    // other up. Everything that touches HKMP is in this file, and nothing
    // here runs unless HKMP is installed and Multiplayer Help-Ups is on (see
    // StumbleMultiplayer.cs), so Stumble works without HKMP.
    //
    // Messages, all sent as HKMP "collections" so several in one network tick
    // add up instead of replacing each other. HKMP reads a collection, then
    // calls the handler once per message in it, so the readers below make
    // collections but every handler takes a single message:
    //   Status   (game -> server -> others in the same room): the stage of this
    //            player's trip (up, falling, lying, getting up). Sent when it
    //            changes and once a second, so others know, even if they
    //            arrive in the room later.
    //   HelpUp   (helper's game -> server -> the fallen player's game): the
    //            helper held Up next to them long enough.
    //   HelpProgress (helper's game -> server -> everyone else in the room):
    //            the helper started or stopped holding Up next to a fallen
    //            player, and how long a full hold takes. Every game, the fallen
    //            player's own included, shows that Knight being lifted towards
    //            a kneel while the hold lasts, and sinking back if it stops.
    //   Struggle (fallen player's game -> server -> everyone else in the room):
    //            they pressed left or right before they could get up alone, so
    //            every game shows their Knight struggling to get up.
    //
    // A fallen Knight also struggles once on a player's screen when that
    // player walks within reach of them. Together with the struggles above,
    // that's how players learn a fallen Knight can be helped up: there's no
    // on-screen prompt.
    //
    // Each game shows other players' trips itself from their Status: HKMP
    // only sends the game's own animations, and skips some (like the held
    // lying pose), so relying on it left fallen Knights walking in place.
    // While a player is down, their pose is put back every frame if anything
    // else, HKMP included, plays over it.
    //
    // HKMP only lets players join a server whose networked add-ons match
    // theirs exactly (name and version), so the version here is the message
    // format, not the mod's version: change it only when the messages change.

    internal static class StumbleHkmp
    {
        internal const string AddonName = "Stumble";
        internal const string ProtocolVersion = "4";

        /// <summary>Registers the client and server add-ons with HKMP. Call before mods finish loading.</summary>
        internal static ICoopLink Register(StumbleMod mod)
        {
            var client = new StumbleClientAddon(mod);
            ClientAddon.RegisterAddon(client);
            ServerAddon.RegisterAddon(new StumbleServerAddon());
            return client;
        }
    }

    // ---- Messages ----------------------------------------------------------

    internal enum ToServerPacket { Status, HelpUp, HelpProgress, Struggle }
    internal enum ToClientPacket { PlayerStatus, HelpedUp, HelpProgress, Struggled }

    /// <summary>The stage of this player's trip.</summary>
    internal sealed class StatusData : IPacketData
    {
        public TripPhase Phase;

        public bool IsReliable => true;
        public bool DropReliableDataIfNewerExists => false;
        public void WriteData(IPacket packet) => packet.Write((byte)Phase);
        public void ReadData(IPacket packet) => Phase = (TripPhase)packet.ReadByte();
    }

    /// <summary>Another player's trip stage, passed on by the server.</summary>
    internal sealed class PlayerStatusData : IPacketData
    {
        public ushort PlayerId;
        public TripPhase Phase;

        public bool IsReliable => true;
        public bool DropReliableDataIfNewerExists => false;
        public void WriteData(IPacket packet) { packet.Write(PlayerId); packet.Write((byte)Phase); }
        public void ReadData(IPacket packet) { PlayerId = packet.ReadUShort(); Phase = (TripPhase)packet.ReadByte(); }
    }

    /// <summary>The sender helped this player up.</summary>
    internal sealed class HelpUpData : IPacketData
    {
        public ushort TargetId;

        public bool IsReliable => true;
        public bool DropReliableDataIfNewerExists => false;
        public void WriteData(IPacket packet) => packet.Write(TargetId);
        public void ReadData(IPacket packet) => TargetId = packet.ReadUShort();
    }

    /// <summary>This player helped you up.</summary>
    internal sealed class HelpedUpData : IPacketData
    {
        public ushort HelperId;

        public bool IsReliable => true;
        public bool DropReliableDataIfNewerExists => false;
        public void WriteData(IPacket packet) => packet.Write(HelperId);
        public void ReadData(IPacket packet) => HelperId = packet.ReadUShort();
    }

    /// <summary>The sender started (or stopped) holding Up next to this fallen player.</summary>
    internal sealed class HelpProgressRequest : IPacketData
    {
        public ushort TargetId;
        public bool Started;
        public float Seconds; // how long a full hold takes, by the helper's settings

        public bool IsReliable => true;
        public bool DropReliableDataIfNewerExists => false;
        public void WriteData(IPacket packet) { packet.Write(TargetId); packet.Write(Started); packet.Write(Seconds); }
        public void ReadData(IPacket packet) { TargetId = packet.ReadUShort(); Started = packet.ReadBool(); Seconds = packet.ReadFloat(); }
    }

    /// <summary>A helper started (or stopped) lifting a fallen player; ForYou when that's this game's Knight.</summary>
    internal sealed class HelpProgressData : IPacketData
    {
        public ushort HelperId;
        public ushort TargetId;
        public bool ForYou;
        public bool Started;
        public float Seconds;

        public bool IsReliable => true;
        public bool DropReliableDataIfNewerExists => false;
        public void WriteData(IPacket packet)
        {
            packet.Write(HelperId); packet.Write(TargetId); packet.Write(ForYou);
            packet.Write(Started); packet.Write(Seconds);
        }
        public void ReadData(IPacket packet)
        {
            HelperId = packet.ReadUShort(); TargetId = packet.ReadUShort(); ForYou = packet.ReadBool();
            Started = packet.ReadBool(); Seconds = packet.ReadFloat();
        }
    }

    /// <summary>This player's Knight struggled to get up. Carries nothing else.</summary>
    internal sealed class StruggleData : IPacketData
    {
        public bool IsReliable => true;
        public bool DropReliableDataIfNewerExists => false;
        public void WriteData(IPacket packet) => packet.Write((byte)0);
        public void ReadData(IPacket packet) => packet.ReadByte();
    }

    /// <summary>Another player's Knight struggled to get up, passed on by the server.</summary>
    internal sealed class PlayerStruggledData : IPacketData
    {
        public ushort PlayerId;

        public bool IsReliable => true;
        public bool DropReliableDataIfNewerExists => false;
        public void WriteData(IPacket packet) => packet.Write(PlayerId);
        public void ReadData(IPacket packet) => PlayerId = packet.ReadUShort();
    }

    // ---- The player's side -------------------------------------------------

    internal sealed class StumbleClientAddon : ClientAddon, ICoopLink
    {
        private const float HeartbeatInterval = 1f;  // seconds between status messages
        private const float HeardTimeout = 3f;       // a player not heard from for this long doesn't count
        private const float HelpRangeX = 1.75f;      // how close a helper must stand, side to side
        private const float HelpRangeY = 1.25f;      // and up and down
        private const float HelpCooldown = 1f;       // after helping someone up, before helping again

        // A hint, not instructions: helping up is meant to be figured out (the
        // README has the plain instructions).
        internal const string ConnectMessage = "Stumble: If a friend is down, you'll know what to say, and what to do.";

        private readonly StumbleMod _mod;
        private IClientApi _api;
        private IClientAddonNetworkSender<ToServerPacket> _sender;

        /// <summary>What this game knows about another player's trip, and what it's showing of it.</summary>
        private sealed class RemoteState
        {
            public TripPhase Phase;                  // their trip stage, from their last Status
            public float LastHeard;
            public TripPhase Shown;                  // the stage this game has started showing on their Knight
            public tk2dSpriteAnimationClip FallClip; // the fall playing on their Knight, if any

            // Being lifted by a helper (this player or another): how long a full
            // hold takes (0 = not being lifted), who's lifting, and the clip.
            public float LiftSeconds;
            public ushort LiftHelper;
            public bool LiftByMe;
            public tk2dSpriteAnimationClip LiftClip;

            public void EndLift()
            {
                LiftSeconds = 0f;
                LiftByMe = false;
                LiftClip = null;
            }

            // A struggle to show next (they struggled, or this player just
            // came within reach), and the one playing.
            public bool StrugglePending;
            public tk2dSpriteAnimationClip StruggleClip;

            public void EndStruggle()
            {
                StrugglePending = false;
                StruggleClip = null;
            }
        }
        private readonly Dictionary<ushort, RemoteState> _remote = new Dictionary<ushort, RemoteState>();

        private TripPhase _phase = TripPhase.Up;
        private float _nextHeartbeat;

        private bool _helping;
        private ushort _helpTarget;
        private string _helpTargetName;
        private float _helpHeld;
        private float _helpCooldownUntil;
        private bool _loggedHelpMiss;  // one "can't help" line per press of Up
        private bool _hasInReach;      // a fallen player was within reach last frame
        private ushort _inReach;       // which one

        public StumbleClientAddon(StumbleMod mod) { _mod = mod; }

        protected override string Name => StumbleHkmp.AddonName;
        protected override string Version => StumbleHkmp.ProtocolVersion;
        public override bool NeedsNetwork => true;

        public override void Initialize(IClientApi clientApi)
        {
            _api = clientApi;
            _sender = clientApi.NetClient.GetNetworkSender<ToServerPacket>(this);

            IClientAddonNetworkReceiver<ToClientPacket> receiver =
                clientApi.NetClient.GetNetworkReceiver<ToClientPacket>(this, id =>
                {
                    switch (id)
                    {
                        case ToClientPacket.PlayerStatus: return new PacketDataCollection<PlayerStatusData>();
                        case ToClientPacket.HelpedUp: return new PacketDataCollection<HelpedUpData>();
                        case ToClientPacket.HelpProgress: return new PacketDataCollection<HelpProgressData>();
                        case ToClientPacket.Struggled: return new PacketDataCollection<PlayerStruggledData>();
                        default: return null;
                    }
                });
            receiver.RegisterPacketHandler<PlayerStatusData>(ToClientPacket.PlayerStatus, OnPlayerStatus);
            receiver.RegisterPacketHandler<HelpedUpData>(ToClientPacket.HelpedUp, OnHelpedUp);
            receiver.RegisterPacketHandler<HelpProgressData>(ToClientPacket.HelpProgress, OnHelpProgress);
            receiver.RegisterPacketHandler<PlayerStruggledData>(ToClientPacket.Struggled, OnPlayerStruggled);

            clientApi.ClientManager.ConnectEvent += OnConnect;
            clientApi.ClientManager.DisconnectEvent += () =>
            {
                _remote.Clear();
                ResetHelp();
                _hasInReach = false;
            };
            clientApi.ClientManager.PlayerDisconnectEvent += player => _remote.Remove(player.Id);
        }

        /// <summary>Connected: send a status straight away, and post the hint about helping up.</summary>
        private void OnConnect()
        {
            _nextHeartbeat = 0f;
            _api.UiManager.ChatBox.AddMessage(ConnectMessage);
        }

        // ---- What the rest of the mod uses (ICoopLink) ----

        public bool Connected => _api != null && _api.NetClient.IsConnected;

        public void SetPhase(TripPhase phase)
        {
            _phase = phase;
            SendStatus();
            _nextHeartbeat = Time.unscaledTime + HeartbeatInterval;
        }

        public void Tick(HeroController hc, bool canHelp)
        {
            if (!Connected)
            {
                ResetHelp();
                return;
            }

            if (Time.unscaledTime >= _nextHeartbeat)
            {
                SendStatus();
                _nextHeartbeat = Time.unscaledTime + HeartbeatInterval;
            }

            ShowOtherPlayersTrips();
            UpdateHelping(hc, canHelp);
        }

        public void Struggled()
        {
            if (!Connected) return;
            try
            {
                _sender.SendCollectionData(ToServerPacket.Struggle, new StruggleData());
            }
            catch (System.InvalidOperationException)
            {
                // Not ready to send; the struggle just won't show on other screens this time.
            }
        }

        // ---- Sending ----

        private void SendStatus()
        {
            if (!Connected) return;
            try
            {
                _sender.SendCollectionData(ToServerPacket.Status, new StatusData { Phase = _phase });
            }
            catch (System.InvalidOperationException)
            {
                // Connected but not yet given an ID by the server; the next
                // heartbeat goes out once it has one.
            }
        }

        // ---- Showing other players' trips ----

        /// <summary>
        /// Every frame: plays each other player's trip on their Knight here,
        /// from the stage they last reported. Falling plays the fall once, then
        /// holds the lying pose; lying holds the pose, putting it back if
        /// anything plays over it; getting up plays the stand-up once. Once
        /// they're up, HKMP's own animations take over again.
        /// </summary>
        private void ShowOtherPlayersTrips()
        {
            foreach (KeyValuePair<ushort, RemoteState> entry in _remote)
            {
                RemoteState state = entry.Value;
                tk2dSpriteAnimator anim = null;
                if (Heard(state)
                    && _api.ClientManager.TryGetPlayer(entry.Key, out IClientPlayer player)
                    && player.IsInLocalScene && player.PlayerObject != null)
                    anim = player.PlayerObject.GetComponent<tk2dSpriteAnimator>();

                if (anim == null)
                {
                    // Not here (or gone quiet): start over when they're back.
                    state.Shown = TripPhase.Up;
                    state.FallClip = null;
                    continue;
                }

                switch (state.Phase)
                {
                    case TripPhase.Falling:
                        state.EndLift();
                        state.EndStruggle();
                        if (state.Shown != TripPhase.Falling)
                        {
                            state.Shown = TripPhase.Falling;
                            state.FallClip = _mod.RemoteFallClip(anim);
                            if (state.FallClip != null)
                            {
                                anim.Play(state.FallClip);
                                break;
                            }
                        }
                        // The fall has finished, or was played over: lie there.
                        if (state.FallClip == null || anim.CurrentClip != state.FallClip || !anim.Playing)
                            HoldLyingPose(anim);
                        break;

                    case TripPhase.Lying:
                        state.Shown = TripPhase.Lying;
                        if (state.LiftSeconds > 0f)
                        {
                            // Being lifted: the lift stops at the kneel by itself.
                            if (state.LiftClip == null)
                                state.LiftClip = _mod.LiftClip(_mod.RemoteRiseClip(anim), state.LiftSeconds);
                            if (state.LiftClip != null)
                            {
                                if (anim.CurrentClip != state.LiftClip) anim.Play(state.LiftClip);
                                state.EndStruggle();
                                break;
                            }
                        }

                        // A struggle to show: play it, then lie back down once it's over.
                        if (state.StrugglePending)
                        {
                            state.StrugglePending = false;
                            state.StruggleClip = _mod.StruggleClip(_mod.RemoteRiseClip(anim));
                            if (state.StruggleClip != null) anim.Play(state.StruggleClip);
                        }
                        if (state.StruggleClip != null && anim.CurrentClip == state.StruggleClip && anim.Playing)
                            break;
                        state.StruggleClip = null;

                        HoldLyingPose(anim);
                        break;

                    case TripPhase.Rising:
                        if (state.Shown != TripPhase.Rising)
                        {
                            state.Shown = TripPhase.Rising;
                            // Lifted to a kneel first: carry on from there.
                            tk2dSpriteAnimationClip rise = _mod.RemoteRiseClip(anim);
                            if (state.LiftClip != null) rise = _mod.RiseFromKneelClip(rise) ?? rise;
                            if (rise != null) anim.Play(rise);
                        }
                        state.EndLift();
                        state.EndStruggle();
                        break;

                    default:
                        state.Shown = TripPhase.Up;
                        state.FallClip = null;
                        state.EndLift();
                        state.EndStruggle();
                        break;
                }
            }
        }

        private void HoldLyingPose(tk2dSpriteAnimator anim)
        {
            tk2dSpriteAnimationClip lying = _mod.RemoteLyingClip(anim);
            if (lying != null && anim.CurrentClip != lying) anim.Play(lying);
        }

        // ---- Helping someone up ----

        /// <summary>
        /// Every frame: when a fallen player first comes within reach (while
        /// the Knight could help them), they struggle once on this screen, as a
        /// hint that they can be helped. While Up is held there, the
        /// fallen Knight is lifted towards a kneel (on every screen) and the
        /// hold counts up to HelpUpTime, then that player's game is told
        /// they've been helped up. Letting go, moving away or losing control
        /// stops the lift and starts the count over.
        /// </summary>
        private void UpdateHelping(HeroController hc, bool canHelp)
        {
            string nearest = null;
            IClientPlayer target = canHelp && Time.time >= _helpCooldownUntil
                ? FallenPlayerInReach(hc, out nearest)
                : null;

            if (target != null && (!_hasInReach || target.Id != _inReach)
                && _remote.TryGetValue(target.Id, out RemoteState reached) && reached.LiftSeconds <= 0f)
                reached.StrugglePending = true;
            _hasInReach = target != null;
            if (target != null) _inReach = target.Id;

            if (!_mod.UpHeld())
            {
                if (_helping) StopHelping("let go of Up");
                _loggedHelpMiss = false;
                return;
            }

            if (Time.time < _helpCooldownUntil)
            {
                if (_helping) StopHelping("just helped someone");
                return;
            }

            if (!canHelp)
            {
                if (_helping) StopHelping("not standing in control any more");
                else if (!_loggedHelpMiss) Note("holding Up, but can't help right now: not standing in control, or down yourself");
                _loggedHelpMiss = true;
                return;
            }

            if (target == null)
            {
                if (_helping) StopHelping("out of reach");
                else if (!_loggedHelpMiss) Note($"holding Up, but no fallen player within reach ({nearest})");
                _loggedHelpMiss = true;
                return;
            }

            if (_helping && target.Id != _helpTarget) StopHelping("moved on to someone else");
            if (!_helping) StartHelping(target);

            _helpHeld += Time.deltaTime;
            if (_helpHeld < _mod.HelpUpTime) return;

            try
            {
                _sender.SendCollectionData(ToServerPacket.HelpUp, new HelpUpData { TargetId = target.Id });
            }
            catch (System.InvalidOperationException)
            {
                return; // not ready to send yet; keep holding and it tries again next frame
            }

            Note($"helped {target.Username} up");
            // They'll be up in a moment: show them carrying on from the kneel
            // here straight away, and don't count them as down meanwhile.
            if (_remote.TryGetValue(target.Id, out RemoteState state)) state.Phase = TripPhase.Rising;
            ResetHelp();
            _helpCooldownUntil = Time.time + HelpCooldown;
        }

        /// <summary>Starts holding Up next to this player: lift them here, and tell everyone else.</summary>
        private void StartHelping(IClientPlayer target)
        {
            _helping = true;
            _helpTarget = target.Id;
            _helpTargetName = target.Username;
            _helpHeld = 0f;
            Note($"holding Up next to {target.Username}...");

            if (_remote.TryGetValue(target.Id, out RemoteState state))
            {
                state.EndLift();
                state.LiftSeconds = _mod.HelpUpTime;
                state.LiftByMe = true;
            }
            SendHelpProgress(target.Id, true);
        }

        /// <summary>Stops holding Up before they were up: they sink back down, here and everywhere else.</summary>
        private void StopHelping(string why)
        {
            Note($"stopped helping {_helpTargetName} up: {why}");
            if (_remote.TryGetValue(_helpTarget, out RemoteState state) && state.LiftByMe) state.EndLift();
            SendHelpProgress(_helpTarget, false);
            ResetHelp();
        }

        private void SendHelpProgress(ushort target, bool started)
        {
            try
            {
                _sender.SendCollectionData(ToServerPacket.HelpProgress,
                    new HelpProgressRequest { TargetId = target, Started = started, Seconds = _mod.HelpUpTime });
            }
            catch (System.InvalidOperationException)
            {
                // Not ready to send; the lift just won't show on other screens this time.
            }
        }

        /// <summary>
        /// The nearest fallen player within reach in this room, or null; when
        /// null, <paramref name="nearest"/> says who's down and how far away,
        /// for the log.
        /// </summary>
        private IClientPlayer FallenPlayerInReach(HeroController hc, out string nearest)
        {
            Vector3 here = hc.transform.position;
            IClientPlayer best = null;
            float bestX = float.MaxValue;
            nearest = "nobody is lying down in this room";
            float nearestDistance = float.MaxValue;

            foreach (KeyValuePair<ushort, RemoteState> entry in _remote)
            {
                if (entry.Value.Phase != TripPhase.Lying || !Heard(entry.Value)) continue;
                if (!_api.ClientManager.TryGetPlayer(entry.Key, out IClientPlayer player)) continue;
                if (!player.IsInLocalScene || player.PlayerObject == null) continue;

                Vector3 there = player.PlayerObject.transform.position;
                float dx = Mathf.Abs(there.x - here.x);
                float dy = Mathf.Abs(there.y - here.y);

                float distance = Vector2.Distance(there, here);
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = $"nearest is {player.Username}, {dx:0.0} across and {dy:0.0} up or down; "
                        + $"reach is {HelpRangeX:0.0} and {HelpRangeY:0.0}";
                }

                if (dx > HelpRangeX || dy > HelpRangeY || dx >= bestX) continue;
                best = player;
                bestX = dx;
            }
            return best;
        }

        private void ResetHelp()
        {
            _helping = false;
            _helpHeld = 0f;
        }

        private static bool Heard(RemoteState state) => Time.unscaledTime - state.LastHeard <= HeardTimeout;

        // ---- Receiving ----

        private void OnPlayerStatus(PlayerStatusData status)
        {
            if (!_remote.TryGetValue(status.PlayerId, out RemoteState state))
            {
                state = new RemoteState();
                _remote[status.PlayerId] = state;
            }
            if (state.Phase != status.Phase) Note($"{PlayerName(status.PlayerId)} is {Describe(status.Phase)}");
            state.Phase = status.Phase;
            state.LastHeard = Time.unscaledTime;
        }

        private void OnHelpedUp(HelpedUpData help)
        {
            string helper = PlayerName(help.HelperId);
            bool wasDown = _mod.HelpedUpBy(helper);
            Note(wasDown ? $"helped up by {helper}" : $"{helper} tried to help, but this Knight isn't down");
        }

        private void OnPlayerStruggled(PlayerStruggledData struggle)
        {
            if (!_remote.TryGetValue(struggle.PlayerId, out RemoteState state)) return;
            if (state.Phase != TripPhase.Lying || state.LiftSeconds > 0f) return;
            state.StrugglePending = true;
            Note($"{PlayerName(struggle.PlayerId)} is struggling to get up");
        }

        private void OnHelpProgress(HelpProgressData help)
        {
            string helper = PlayerName(help.HelperId);

            if (help.ForYou)
            {
                if (help.Started)
                    Note(_mod.LiftStartedBy(help.HelperId, help.Seconds)
                        ? $"{helper} is helping this Knight up..."
                        : $"{helper} started helping, but this Knight isn't lying down");
                else
                {
                    _mod.LiftStoppedBy(help.HelperId);
                    Note($"{helper} stopped helping this Knight up");
                }
                return;
            }

            // Someone else being helped: show the lift on their Knight here.
            if (!_remote.TryGetValue(help.TargetId, out RemoteState state)) return;
            if (help.Started)
            {
                state.EndLift();
                state.LiftSeconds = help.Seconds;
                state.LiftHelper = help.HelperId;
            }
            else if (!state.LiftByMe && state.LiftHelper == help.HelperId)
            {
                state.EndLift();
            }
        }

        // ---- Logging (DetailedLogging only), tagged with this player's name ----

        private void Note(string message)
        {
            if (_mod.DetailedLoggingOn) _mod.LogMultiplayer($"[{_api.ClientManager.Username}] {message}");
        }

        private string PlayerName(ushort id) =>
            _api.ClientManager.TryGetPlayer(id, out IClientPlayer player) ? player.Username : $"player {id}";

        private static string Describe(TripPhase phase)
        {
            switch (phase)
            {
                case TripPhase.Falling: return "falling over";
                case TripPhase.Lying: return "lying down";
                case TripPhase.Rising: return "getting up";
                default: return "up";
            }
        }
    }

    // ---- The server's side -------------------------------------------------
    //
    // Only passes messages on, to players in the same room as the sender. Runs
    // on the server's network thread, so it never touches the game itself.

    internal sealed class StumbleServerAddon : ServerAddon
    {
        private IServerManager _players;
        private IServerAddonNetworkSender<ToClientPacket> _sender;

        protected override string Name => StumbleHkmp.AddonName;
        protected override string Version => StumbleHkmp.ProtocolVersion;
        public override bool NeedsNetwork => true;

        public override void Initialize(IServerApi serverApi)
        {
            _players = serverApi.ServerManager;
            _sender = serverApi.NetServer.GetNetworkSender<ToClientPacket>(this);

            IServerAddonNetworkReceiver<ToServerPacket> receiver =
                serverApi.NetServer.GetNetworkReceiver<ToServerPacket>(this, id =>
                {
                    switch (id)
                    {
                        case ToServerPacket.Status: return new PacketDataCollection<StatusData>();
                        case ToServerPacket.HelpUp: return new PacketDataCollection<HelpUpData>();
                        case ToServerPacket.HelpProgress: return new PacketDataCollection<HelpProgressRequest>();
                        case ToServerPacket.Struggle: return new PacketDataCollection<StruggleData>();
                        default: return null;
                    }
                });
            receiver.RegisterPacketHandler<StatusData>(ToServerPacket.Status, OnStatus);
            receiver.RegisterPacketHandler<HelpUpData>(ToServerPacket.HelpUp, OnHelpUp);
            receiver.RegisterPacketHandler<HelpProgressRequest>(ToServerPacket.HelpProgress, OnHelpProgress);
            receiver.RegisterPacketHandler<StruggleData>(ToServerPacket.Struggle, OnStruggle);
        }

        private void OnStatus(ushort fromId, StatusData status)
        {
            if (!_players.TryGetPlayer(fromId, out IServerPlayer from)) return;

            ushort[] sameRoom = _players.Players
                .Where(p => p.Id != fromId && p.CurrentScene == from.CurrentScene)
                .Select(p => p.Id)
                .ToArray();
            if (sameRoom.Length == 0) return;

            _sender.SendCollectionData(ToClientPacket.PlayerStatus,
                new PlayerStatusData { PlayerId = fromId, Phase = status.Phase },
                sameRoom);
        }

        private void OnHelpUp(ushort fromId, HelpUpData help)
        {
            if (!_players.TryGetPlayer(fromId, out IServerPlayer from)) return;
            if (!_players.TryGetPlayer(help.TargetId, out IServerPlayer target)) return;
            if (target.CurrentScene != from.CurrentScene) return;
            _sender.SendCollectionData(ToClientPacket.HelpedUp, new HelpedUpData { HelperId = fromId }, target.Id);
        }

        /// <summary>Passes a fallen player's struggle to everyone else in the room.</summary>
        private void OnStruggle(ushort fromId, StruggleData struggle)
        {
            if (!_players.TryGetPlayer(fromId, out IServerPlayer from)) return;

            ushort[] sameRoom = _players.Players
                .Where(p => p.Id != fromId && p.CurrentScene == from.CurrentScene)
                .Select(p => p.Id)
                .ToArray();
            if (sameRoom.Length == 0) return;

            _sender.SendCollectionData(ToClientPacket.Struggled, new PlayerStruggledData { PlayerId = fromId }, sameRoom);
        }

        /// <summary>Passes a helper's start or stop to the fallen player, and to everyone else in the room.</summary>
        private void OnHelpProgress(ushort fromId, HelpProgressRequest help)
        {
            if (!_players.TryGetPlayer(fromId, out IServerPlayer from)) return;
            if (!_players.TryGetPlayer(help.TargetId, out IServerPlayer target)) return;
            if (target.CurrentScene != from.CurrentScene) return;

            _sender.SendCollectionData(ToClientPacket.HelpProgress, new HelpProgressData
            {
                HelperId = fromId, TargetId = help.TargetId, ForYou = true,
                Started = help.Started, Seconds = help.Seconds
            }, target.Id);

            ushort[] bystanders = _players.Players
                .Where(p => p.Id != fromId && p.Id != help.TargetId && p.CurrentScene == from.CurrentScene)
                .Select(p => p.Id)
                .ToArray();
            if (bystanders.Length == 0) return;

            _sender.SendCollectionData(ToClientPacket.HelpProgress, new HelpProgressData
            {
                HelperId = fromId, TargetId = help.TargetId, ForYou = false,
                Started = help.Started, Seconds = help.Seconds
            }, bystanders);
        }
    }
}

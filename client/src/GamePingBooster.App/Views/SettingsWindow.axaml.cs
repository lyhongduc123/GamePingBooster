using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using GamePingBooster.App.Services;
using GamePingBooster.App.ViewModels;
using GamePingBooster.Core.Ipc;
using GamePingBooster.App.Services.Localization;

namespace GamePingBooster.App.Views;

public partial class SettingsWindow : SurfaceWindow
{
    private PipeClient? _pipe;

    /// <summary>
    /// The verbs still owed a reply, and the first thing any of them complained about.
    ///
    /// A set rather than a flag, because one Save can now send two commands that each have to be
    /// answered: the relays and the server address are separate files in separate parts of the
    /// config, and a player who changed both has to hear about both. Reading only the first reply
    /// would close the window on the relay's success while the address save was still writing, and
    /// a failure in the second would then be reported nowhere.
    /// </summary>
    private readonly HashSet<string> _awaitingReplies = new(StringComparer.Ordinal);

    /// <summary>
    /// The same verbs in the order they went out. Kept because <see cref="_awaitingReplies"/> loses
    /// the order as it empties, and the order is the order two complaints are put back together in.
    /// </summary>
    private readonly List<string> _sentVerbs = [];

    /// <summary>What each answered verb complained about, filed under the verb itself.</summary>
    private readonly Dictionary<string, string> _replyErrors = new(StringComparer.Ordinal);

    public SettingsWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Listens on the same pipe as the main window.
    ///
    /// One reader raises the event to every handler, so a second subscriber costs nothing and
    /// races with nothing - and without it this window would have to report success the moment
    /// it sent the message, which would mean claiming a save that the service may well have
    /// rejected.
    /// </summary>
    public void Attach(PipeClient pipe)
    {
        _pipe = pipe;
        pipe.StatusReceived += OnStatus;
        Closed += (_, _) =>
        {
            pipe.StatusReceived -= OnStatus;
            // Cancelled out of a Save that is still in flight, so the replies it is owed must not
            // be remembered. This window is shown again by ShowSettings, and a verb left on the
            // books here would be settled by the next Save's replies instead of this one's.
            ForgetAwaitingReplies();
        };
    }

    private void OnStatus(StatusMessage status)
    {
        string? failure;
        lock (_awaitingReplies)
        {
            if (_awaitingReplies.Count == 0) return;
            if (status.AckVerb is not { } verb || !_awaitingReplies.Remove(verb)) return;

            // Kept per verb rather than collapsed into one message, because two commands can both
            // fail and a Save that reported only the first would be a Save that stayed quiet about
            // half of itself. Both are awaited either way, so nothing is reported until the last
            // one has settled.
            if (!string.IsNullOrWhiteSpace(status.CommandError)) _replyErrors[verb] = status.CommandError;
            if (_awaitingReplies.Count > 0) return;

            // Read here, under the lock, and handed to the closure below as a value: the UI thread
            // holds no lock of its own, and the next Save must not be able to change this out from
            // under the message it is about to show.
            failure = _replyErrors.Count == 0
                ? null
                : string.Join(Environment.NewLine, _sentVerbs
                    .Where(_replyErrors.ContainsKey)
                    .Select(v => _replyErrors[v]));
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is not SettingsViewModel vm) return;

            // CommandError, not Error. This one is about the messages just sent; Error is about
            // the tunnel, and a relay that is unreachable, in a different auth mode, or refusing
            // the key it holds has no bearing on whether settings were saved. Connect is what
            // asks the network anything - and it still reports all of that, in the main window.
            if (failure is not null)
            {
                vm.Error = failure;
                vm.Saved = false;
                return;
            }
            vm.Error = null;
            vm.Saved = true;

            // Close on success. The banner was there so the user could see it worked, but a
            // dialog that stays open after doing its job reads as one that did not.
            Close();
        });
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm || _pipe is null) return;

        vm.Error = null;
        vm.Saved = false;

        if (!_pipe.IsConnected)
        {
            vm.Error = Loc.T("settings.noService");
            return;
        }

        // Both commands are collected before either is sent, so the decision about what this Save
        // is asking for is made from the screen's state and not from anything that could have
        // changed underneath it while the first await was in flight.
        var sendRelay = vm.RelaySettingsChanged;
        var sendServerAddress = vm.ServerAddressSettingsChanged;

        try
        {
            // Its own verb, sent first and not awaited for a verdict: it is a single switch the
            // service saves on its own, and the saves below still decide whether this window
            // reports success.
            if (vm.QualitySharingChanged)
            {
                await _pipe.SendAsync(new CommandMessage { Verb = "set-quality-sharing", Enabled = vm.QualitySharing });
            }

            // Nothing for the service to write, so nothing to send and nothing to wait for. This
            // screen also carries the language, which this app saves by itself the moment it is
            // picked - and sending a command for that made the service reload its profile, which
            // fails on a machine that has not signed in yet. A save that changed only the
            // language was reported as a failure because of a file it never needed.
            if (!sendRelay && !sendServerAddress)
            {
                vm.Saved = true;
                Close();
                return;
            }

            lock (_awaitingReplies)
            {
                _awaitingReplies.Clear();
                _sentVerbs.Clear();
                _replyErrors.Clear();
                // Registered before the send, never after: the reply can arrive while SendAsync is
                // still returning, and a verb added afterwards would miss it and leave the window
                // open forever. In _sentVerbs as well, because the order the two go out in is the
                // order they are reported back in.
                if (sendRelay) RegisterAwaitingReply("set-relay");
                if (sendServerAddress) RegisterAwaitingReply("set-server-address");
            }

            if (sendRelay)
            {
                await _pipe.SendAsync(new CommandMessage
                {
                    Verb = "set-relay",
                    RelayEndpoints = vm.EndpointList,
                    // Blank means "keep the key already stored", which the service understands. Send
                    // it as null rather than an empty string so the intent is unambiguous on the
                    // other side.
                    Psk = string.IsNullOrWhiteSpace(vm.Psk) ? null : vm.Psk,
                    // Sent as a string every time, never null: the box's contents ARE the intent, so
                    // an emptied box has to clear the setting. Null is reserved for callers that
                    // mean "leave it alone", and this screen never means that - it shows the current
                    // value, so whatever is in it is what the user decided.
                    LicenceUrl = vm.LicenceUrl.Trim(),
                });
            }

            if (sendServerAddress)
            {
                // Sent last, and with the game named rather than inferred: the screen knows which
                // game's server the player is filling in, and the service checks that name against
                // the profile before it writes anything. An empty box is a real value here - it is
                // how a player says they have no server of their own - so the list goes over as an
                // empty list rather than not being sent.
                await _pipe.SendAsync(new CommandMessage
                {
                    Verb = "set-server-address",
                    GameId = SettingsViewModel.ServerAddressGameId,
                    ServerAddresses = vm.ServerAddressList,
                });
            }
        }
        catch (Exception ex)
        {
            ForgetAwaitingReplies();
            vm.Error = Loc.F("settings.serviceUnreachable", ex.Message);
        }
    }

    /// <summary>
    /// Records a verb this Save is waiting on. Callers hold <see cref="_awaitingReplies"/>'s lock:
    /// these two collections and the message built from them are one piece of state, and a reply
    /// arriving mid-registration must not be able to see half of a Save.
    /// </summary>
    private void RegisterAwaitingReply(string verb)
    {
        _awaitingReplies.Add(verb);
        _sentVerbs.Add(verb);
    }

    /// <summary>Drops everything this Save was waiting on, whether it settled or was abandoned.</summary>
    private void ForgetAwaitingReplies()
    {
        lock (_awaitingReplies)
        {
            _awaitingReplies.Clear();
            _sentVerbs.Clear();
            _replyErrors.Clear();
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}

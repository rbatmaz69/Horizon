using Horizon.Net;
using UnityEngine;
using UnityEngine.UI;

namespace Horizon.Game
{
    /// <summary>
    /// The two menu pages that open a room and show who is in it.
    ///
    /// <para>Built and wired by <c>MenuUiSetup</c> like every other page here, and holding no layout
    /// of its own — what it does is turn <see cref="NetSession"/>'s state into words and rows, and
    /// turn taps back into calls on it.</para>
    ///
    /// <para><b>Browsing starts when the page opens and stops when it closes.</b> Listening for hosts
    /// means holding Android's Wi-Fi multicast lock, which costs battery; a session that browsed for as
    /// long as the app was running would pay for it on every drive. <see cref="Open"/> is bound
    /// alongside the page navigation on the button that gets here, which is the arrangement
    /// <c>StartScreen.ShowChosenPlace</c> already uses.</para>
    ///
    /// <para><b>Every label is assigned only when its text has actually changed.</b> These pages are
    /// open while the game is paused, so it is not driving code — but a row rebuilt sixty times a
    /// second to say the same thing is garbage either way, and the rule the rev counter follows costs
    /// one comparison.</para>
    /// </summary>
    public sealed class MultiplayerScreen : MonoBehaviour
    {
        [SerializeField] private NetSession session;
        [SerializeField] private MenuPanels panels;

        [Header("Multiplayer page")]
        [SerializeField] private Text status;
        [SerializeField] private InputField nameField;
        [SerializeField] private Button[] hostRows = new Button[0];
        [SerializeField] private Text[] hostLabels = new Text[0];
        [SerializeField] private InputField addressField;

        [Header("Room page")]
        [SerializeField] private Text roomStatus;
        [SerializeField] private Button[] playerRows = new Button[0];
        [SerializeField] private Text[] playerLabels = new Text[0];

        private string statusShown = string.Empty;
        private string roomStatusShown = string.Empty;
        private readonly string[] rowShown = new string[16];
        private bool open;

        private void Awake()
        {
            if (session == null)
            {
                session = FindFirstObjectByType<NetSession>();
            }

            if (nameField != null)
            {
                nameField.characterLimit = PlayerChoices.MaxNameLength;
                nameField.text = PlayerChoices.Name;
                nameField.onEndEdit.AddListener(OnNameChanged);
            }
        }

        private void OnDestroy()
        {
            nameField?.onEndEdit.RemoveListener(OnNameChanged);
        }

        /// <summary>Bound beside the page navigation on whatever button opens this page.</summary>
        public void Open()
        {
            if (nameField != null)
            {
                nameField.SetTextWithoutNotify(PlayerChoices.Name);
            }

            // The button that gets here navigates to the join page first and then calls this, in that
            // order, because a persistent listener list fires in the order it was built. That is what
            // lets one button lead to two different pages: which one is right depends on whether there
            // is already a room, and a saved event cannot ask.
            if (session.InRoom)
            {
                open = false;
                panels?.Show((int)MenuPage.Room);
                return;
            }

            open = true;
            session.StartBrowsing();
        }

        /// <summary>Bound to Back, and to anything else that leaves the page.</summary>
        public void Close()
        {
            open = false;
            session?.StopBrowsing();
        }

        public void HostGame()
        {
            CommitName();
            session.StopBrowsing();
            session.HostGame();
            ShowRoomIfIn();
        }

        /// <summary>Joins whatever is typed in the address field.</summary>
        public void JoinTyped()
        {
            if (addressField == null)
            {
                return;
            }

            CommitName();
            session.StopBrowsing();
            session.JoinGame(addressField.text);
            ShowRoomIfIn();
        }

        /// <summary>
        /// Joins the host on one of the discovered rows.
        ///
        /// <para>Eight separate no-argument methods rather than one taking an index, because a
        /// persistent UnityEvent saved into a scene can carry an int — but the rows are rebuilt from a
        /// list that changes while the page is open, so the row a listener was bound to is not the host
        /// it is showing. The index is read here, at the moment of the tap, off what is on screen.</para>
        /// </summary>
        public void JoinRow0() => JoinRow(0);

        public void JoinRow1() => JoinRow(1);

        public void JoinRow2() => JoinRow(2);

        public void JoinRow3() => JoinRow(3);

        public void JoinRow4() => JoinRow(4);

        public void JoinRow5() => JoinRow(5);

        private void JoinRow(int index)
        {
            if (index >= session.FoundHostCount)
            {
                return;
            }

            NetSession.FoundHost host = session.FoundHostAt(index);
            CommitName();
            session.StopBrowsing();
            session.JoinGame(host.Address);
            ShowRoomIfIn();
        }

        public void LeaveRoom()
        {
            session.Leave();
            panels?.Show((int)MenuPage.Multiplayer);
            Open();
        }

        private void ShowRoomIfIn()
        {
            if (session.InRoom)
            {
                open = false;
                panels?.Show((int)MenuPage.Room);
            }
        }

        private void OnNameChanged(string value)
        {
            PlayerChoices.Name = value != null ? value.Trim() : string.Empty;
            PlayerChoices.Save();
        }

        private void CommitName()
        {
            if (nameField != null)
            {
                OnNameChanged(nameField.text);
            }
        }

        private void Update()
        {
            if (session == null)
            {
                return;
            }

            // Each page is refreshed only while it is actually on screen, and that is not tidiness:
            // both of these compose a status line, and a string built every frame to be compared
            // against the one already shown is garbage. The room page in particular would otherwise be
            // doing it for the whole of a drive, which is driving code.
            //
            // Asked of the label rather than of MenuPanels, because "is this page visible" is a
            // question the object itself answers and a page index is a second copy of it.
            if (open && status != null && status.gameObject.activeInHierarchy)
            {
                RefreshJoinPage();
            }

            if (roomStatus != null && roomStatus.gameObject.activeInHierarchy)
            {
                RefreshRoomPage();
            }
        }

        private void RefreshJoinPage()
        {
            Set(status, ref statusShown, JoinStatusText());

            int hosts = session.FoundHostCount;

            for (int i = 0; i < hostRows.Length; i++)
            {
                bool used = i < hosts;

                if (hostRows[i] != null && hostRows[i].gameObject.activeSelf != used)
                {
                    hostRows[i].gameObject.SetActive(used);
                }

                if (!used || i >= hostLabels.Length)
                {
                    continue;
                }

                NetSession.FoundHost host = session.FoundHostAt(i);

                // A host on another build is listed and not hidden. Hiding it is the failure this
                // project keeps naming: the friend is there, the app can see them, and a list that
                // simply stayed empty would send both of them looking at the network.
                string caption = host.Compatible
                    ? $"{host.Name}  ·  {host.Players}/{NetProtocol.MaxPeers}"
                    : $"{host.Name}  ·  different version";

                Set(hostLabels[i], ref rowShown[i], caption);

                if (hostRows[i] != null)
                {
                    hostRows[i].interactable = host.Compatible;
                }
            }
        }

        private void RefreshRoomPage()
        {
            Set(roomStatus, ref roomStatusShown, RoomStatusText());

            int shown = 0;

            for (int peer = 0; peer < NetProtocol.MaxPeers && shown < playerRows.Length; peer++)
            {
                PeerInfo info = session.PeerAt(peer);

                if (!info.InUse)
                {
                    continue;
                }

                if (playerRows[shown] != null && !playerRows[shown].gameObject.activeSelf)
                {
                    playerRows[shown].gameObject.SetActive(true);
                }

                if (shown < playerLabels.Length)
                {
                    string caption = peer == NetProtocol.HostPeerId
                        ? $"{info.Name}  ·  hosting"
                        : info.Name;

                    if (peer == session.LocalPeerId)
                    {
                        caption += "  ·  you";
                    }

                    Set(playerLabels[shown], ref rowShown[8 + shown], caption);
                }

                shown++;
            }

            for (int i = shown; i < playerRows.Length; i++)
            {
                if (playerRows[i] != null && playerRows[i].gameObject.activeSelf)
                {
                    playerRows[i].gameObject.SetActive(false);
                }
            }
        }

        private string JoinStatusText()
        {
            if (session.WasRejected)
            {
                switch (session.RejectedBecause)
                {
                    case NetReject.Build:
                        return "That game is running a different version of Horizon.\n"
                               + "Both of you need the same build.";
                    case NetReject.Protocol:
                        return "That game speaks a different protocol.";
                    case NetReject.Full:
                        return "That game is full.";
                }
            }

            if (!string.IsNullOrEmpty(session.LastError))
            {
                return session.LastError;
            }

            if (session.IsBrowsing)
            {
                return session.FoundHostCount > 0
                    ? "Tap a game to join it."
                    : "Looking for games on this network.\n"
                      + "Both devices have to be on the same Wi-Fi.";
            }

            return "Host a game, or join one.";
        }

        private string RoomStatusText()
        {
            if (!session.InRoom)
            {
                return "Not in a game.";
            }

            if (session.Role == NetRole.Host)
            {
                string address = session.LocalAddress;

                return string.IsNullOrEmpty(address)
                    ? $"Hosting.  {session.PeerCount} of {NetProtocol.MaxPeers}."
                    : $"Hosting at {address}.  {session.PeerCount} of {NetProtocol.MaxPeers}.";
            }

            if (session.Admitted)
            {
                return $"In a game.  {session.PeerCount} of {NetProtocol.MaxPeers}.";
            }

            // Still asking, and nothing has come back. See NetSession.JoiningFor for why silence is
            // the only symptom there is.
            return session.JoiningFor > NetSession.JoinPatience
                ? "Nothing is answering at that address.\n"
                  + "Check the number, and that both devices are on the same Wi-Fi."
                : "Joining...";
        }

        private static void Set(Text label, ref string shown, string value)
        {
            if (label == null || shown == value)
            {
                return;
            }

            shown = value;
            label.text = value;
        }

        /// <summary>
        /// Wired by the setup tool. Nothing else may call it.
        ///
        /// <para>The Host, Join and Leave buttons are deliberately <b>not</b> here. They carry
        /// persistent listeners onto the three methods above and this class never reads them back — a
        /// <c>[SerializeField]</c> that is assigned and never used looks exactly like a dependency
        /// while being a decoration, which is the pass that took the atmosphere reference out of
        /// <c>WeatherDirector</c>.</para>
        /// </summary>
        public void SetParts(
            NetSession netSession,
            MenuPanels menuPanels,
            Text joinStatus,
            InputField name,
            Button[] rows,
            Text[] rowLabels,
            InputField address,
            Text roomLabel,
            Button[] players,
            Text[] playerCaptions)
        {
            session = netSession;
            panels = menuPanels;
            status = joinStatus;
            nameField = name;
            hostRows = rows;
            hostLabels = rowLabels;
            addressField = address;
            roomStatus = roomLabel;
            playerRows = players;
            playerLabels = playerCaptions;
        }
    }
}

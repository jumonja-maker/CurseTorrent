//
// StartingMode.cs
//
// Authors:
//   Alan McGovern <alan.mcgovern@gmail.com>
//
// Copyright (C) 2019 Alan McGovern
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//


using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using MonoTorrent.BEncoding;
using MonoTorrent.Logging;
using MonoTorrent.Messages.Peer;

using ReusableTasks;

namespace MonoTorrent.Client.Modes
{
    class StartingMode : IMode
    {
        static readonly Logger Log = Logger.Create (nameof (StartingMode));

        public bool CanAcceptConnections => false;
        public bool CanHandleMessages => false;
        public bool CanHashCheck => true;
        public TorrentState State => TorrentState.Starting;
        public CancellationToken Token => Cancellation.Token;

        CancellationTokenSource Cancellation { get; }
        ConnectionManager ConnectionManager { get; }
        DiskManager DiskManager { get; }
        TorrentManager Manager { get; }
        EngineSettings Settings { get; }
        long LifecycleVersion { get; }
        bool IsCurrentLifecycle => Manager.LifecycleVersion == LifecycleVersion;

        public StartingMode (TorrentManager manager, DiskManager diskManager, ConnectionManager connectionManager, EngineSettings settings)
        {
            (Cancellation, Manager, DiskManager, ConnectionManager, Settings) = (new CancellationTokenSource (), manager, diskManager, connectionManager, settings);
            LifecycleVersion = manager.LifecycleVersion;
        }

        public void Dispose ()
            => Cancellation.Cancel ();

        public void HandleFilePriorityChanged (ITorrentManagerFile file, Priority oldPriority)
        {
            // Nothing
        }

        public void HandleMessage (PeerId id, PeerMessage message, PeerMessage.Releaser releaser)
            => throw new NotSupportedException ();

        public void HandlePeerConnected (PeerId id)
            => throw new NotSupportedException ();

        public void HandlePeerDisconnected (PeerId id)
            => throw new NotSupportedException ();

        public bool ShouldConnect (Peer peer)
            => false;

        public void Tick (int counter)
        {
        }

        public async Task WaitForStartingToComplete ()
        {
            if (!Manager.HasMetadata)
                throw new TorrentException ("Torrents with no metadata must use 'MetadataMode', not 'StartingMode'.");

            try {
                await CreateEmptyFiles ();
                if (!IsCurrentLifecycle) return;
                await VerifyHashState ();
                if (!IsCurrentLifecycle) return;
                Manager.PieceManager.Initialise ();

                if (!Manager.HashChecked) {
                    // The internal hash phase intentionally replaces this mode. Its
                    // cancellation token is therefore not our lifecycle identity.
                    await Manager.HashCheckAsync (false, false);
                    if (!IsCurrentLifecycle) return;
                }

                if (!Manager.HashChecked) {
                    if (Manager.Mode == this)
                        Manager.Mode = new StoppedMode ();
                    return;
                }

                await Manager.RefreshPartialDownloadFilePaths (0, Manager.Files.Count, allowDuringHashing: true);
                if (!IsCurrentLifecycle) return;

                foreach (var peer in Manager.Peers.AvailablePeers)
                    peer.MaybeStale = true;
                SendAnnounces ();

                if (Manager.Complete)
                    await Manager.MaybeWriteFastResumeAsync ();
                else
                    await Manager.MaybeDeleteFastResumeAsync ();
                if (!IsCurrentLifecycle) return;

                Manager.PieceManager.Initialise ();
                if (Manager.PendingV2PieceHashes.TrueCount > 0)
                    await TryLoadV2HashesFromCache ();
                if (!IsCurrentLifecycle) return;

                if (Manager.PendingV2PieceHashes.TrueCount > 0)
                    Manager.Mode = new PieceHashesMode (Manager, DiskManager, ConnectionManager, Settings, false);
                else if (Manager.Complete && Manager.Settings.AllowInitialSeeding && ClientEngine.SupportsInitialSeed)
                    Manager.Mode = new InitialSeedingMode (Manager, DiskManager, ConnectionManager, Settings);
                else
                    Manager.Mode = new DownloadMode (Manager, DiskManager, ConnectionManager, Settings);

                await Manager.DhtAnnounceAsync ();
                if (IsCurrentLifecycle)
                    await Manager.LocalPeerAnnounceAsync ();
            } catch (OperationCanceledException) when (!IsCurrentLifecycle) {
                // A later stop/hash operation owns the manager now.
            } catch (Exception ex) {
                if (IsCurrentLifecycle)
                    Manager.TrySetError (Reason.ReadFailure, ex);
            }
        }

        async ReusableTask CreateEmptyFiles ()
        {
            foreach (var file in Manager.Files) {
                if (!IsCurrentLifecycle) return;
                if (file.Length == 0 && file.Priority != Priority.DoNotDownload)
                    await DiskManager.CreateEmptyFileAsync (file);
            }
        }

        async void SendAnnounces ()
        {
            try {
                // We need to announce before going into Downloading mode, otherwise we will
                // send a regular announce instead of a 'Started' announce.
                await Task.WhenAll (
                    Manager.TrackerManager.ScrapeAsync (CancellationToken.None).AsTask (),
                    Manager.TrackerManager.AnnounceAsync (TorrentEvent.Started, CancellationToken.None).AsTask ()
                );
            } catch (Exception ex) {
                Log.Exception (ex, "Error announcing/scraping while starting the torrent");
            }
        }

        async Task TryLoadV2HashesFromCache ()
        {
            try {
                await MainLoop.SwitchThread ();

                var path = Settings.GetV2HashesPath (Manager.InfoHashes);
                if (File.Exists (path)) {
                    var data = BEncodedValue.Decode<BEncodedDictionary> (File.ReadAllBytes (path));
                    Manager.PieceHashes = Manager.Torrent!.CreatePieceHashes (data.ToDictionary (t => MerkleRoot.FromMemory (t.Key.AsMemory ()), kvp => ReadOnlyMerkleTree.FromLayer (Manager.Torrent.PieceLength, ((BEncodedString) kvp.Value).Span)));
                    Manager.PendingV2PieceHashes.SetAll (false);
                }
            } catch (Exception ex) {
                Log.ExceptionFormated (ex, "Could not load hashes for {0}", Manager.Name);
            }
        }

        async Task VerifyHashState ()
        {
            // If we do not have metadata or the torrent needs a hash check, fast exit.
            if (!Manager.HashChecked)
                return;

            foreach (ITorrentManagerFile file in Manager.Files) {
                if (!file.BitField.AllFalse && file.Length > 0) {
                    if (!await DiskManager.CheckFileExistsAsync (file)) {
                        Manager.SetNeedsHashCheck ();
                        break;
                    }
                }
            }
        }
    }
}

namespace FoxSense.Game;

using FoxSense.Core;

/// <summary>
/// Reads all game entities and caches them as a snapshot.
/// NO team filtering here — features (ESP/aimbot) handle their own filtering.
/// </summary>
public sealed class GameState
{
    private readonly Memory _mem;
    private List<PlayerData> _players = new();
    private readonly object _lock = new();

    public ViewMatrix Matrix { get; private set; }
    public int LocalTeam { get; private set; }
    public long LocalPawn { get; private set; }
    public Vector3 LocalPosition { get; private set; }
    public bool InGame { get; private set; }

    public GameState(Memory mem) => _mem = mem;

    public List<PlayerData> GetPlayers()
    {
        lock (_lock) return new List<PlayerData>(_players);
    }

    public void Update(int screenW, int screenH)
    {
        if (!_mem.IsAttached) { InGame = false; return; }

        try
        {
            // Read view matrix
            var vm = _mem.Read<ViewMatrix>(_mem.ClientAddr(Offsets.dwViewMatrix));
            Matrix = vm;

            // Read local player
            long localPawn = _mem.Read<long>(_mem.ClientAddr(Offsets.dwLocalPlayerPawn));
            LocalPawn = localPawn;

            if (localPawn == 0) { InGame = false; return; }
            InGame = true;

            int localTeam = _mem.ReadByte(localPawn + Offsets.m_iTeamNum);
            LocalTeam = localTeam;
            LocalPosition = _mem.Read<Vector3>(localPawn + Offsets.m_vOldOrigin);

            // Read entity list
            long entityList = _mem.Read<long>(_mem.ClientAddr(Offsets.dwEntityList));
            long listEntry = EntityResolver.GetListEntry(_mem, entityList);
            if (listEntry == 0) return;

            var newPlayers = new List<PlayerData>(16);

            for (int i = 0; i < 64; i++)
            {
                long controller = EntityResolver.GetController(_mem, listEntry, i);
                if (controller == 0) continue;

                int handle = EntityResolver.ReadPawnHandle(_mem, controller);
                if (handle == 0 || handle == -1) continue;

                long pawn = EntityResolver.ResolvePawn(_mem, entityList, handle);
                if (pawn == 0 || pawn == localPawn) continue;

                int health = _mem.Read<int>(pawn + Offsets.m_iHealth);
                if (health <= 0 || health > 100) continue;

                int team = _mem.ReadByte(pawn + Offsets.m_iTeamNum);
                if (team < 2 || team > 3) continue;
                // NO team filtering — let ESP/aimbot decide

                var feetPos = _mem.Read<Vector3>(pawn + Offsets.m_vOldOrigin);
                if (feetPos.IsZero) continue;

                var headPos = new Vector3(feetPos.X, feetPos.Y, feetPos.Z + Offsets.PLAYER_HEIGHT);

                // Project to screen
                bool feetOk = vm.WorldToScreen(feetPos, out var sf, screenW, screenH);
                bool headOk = vm.WorldToScreen(headPos, out var sh, screenW, screenH);
                bool onScreen = feetOk && headOk;

                // Read player name
                string name = "";
                try { name = _mem.ReadString(controller + Offsets.m_iszPlayerName, 32); }
                catch { /* ignore */ }

                // Read bones
                var boneScreen = new Vector3[PlayerData.MAX_BONES];
                var boneValid = new bool[PlayerData.MAX_BONES];

                long gsn = _mem.Read<long>(pawn + Offsets.m_pGameSceneNode);
                if (gsn != 0)
                {
                    long boneArray = _mem.Read<long>(gsn + Offsets.m_modelState + 0x80);
                    if (boneArray != 0)
                    {
                        foreach (var (from, to) in Offsets.BoneConnections)
                        {
                            ReadBone(vm, boneArray, from, boneScreen, boneValid,
                                     screenW, screenH, feetPos);
                            ReadBone(vm, boneArray, to, boneScreen, boneValid,
                                     screenW, screenH, feetPos);
                        }
                    }
                }

                newPlayers.Add(new PlayerData
                {
                    PawnAddress = pawn,
                    Health = health,
                    Team = team,
                    Name = name,
                    FeetPos = feetPos,
                    HeadPos = headPos,
                    ScreenFeet = sf,
                    ScreenHead = sh,
                    OnScreen = onScreen,
                    BoneScreen = boneScreen,
                    BoneValid = boneValid,
                });
            }

            lock (_lock) _players = newPlayers;
        }
        catch
        {
            // Swallow read errors — game might be transitioning
        }
    }

    private void ReadBone(ViewMatrix vm, long boneArray, int boneId,
        Vector3[] boneScreen, bool[] boneValid, int screenW, int screenH,
        Vector3 feetPos)
    {
        if (boneId >= PlayerData.MAX_BONES || boneValid[boneId]) return;

        var world = _mem.Read<Vector3>(
            boneArray + boneId * Offsets.BONE_STRIDE + Offsets.BONE_POS_OFFSET);

        // Validate: bone must be within 150 units of player feet
        // This filters out garbled data from wrong bone array offsets
        if (world.IsZero) return;
        float dist = feetPos.DistanceTo(world);
        if (dist > 150f) return;

        if (vm.WorldToScreen(world, out var screen, screenW, screenH))
        {
            boneScreen[boneId] = screen;
            boneValid[boneId] = true;
        }
    }
}

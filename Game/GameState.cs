namespace FoxSense.Game;

using FoxSense.Core;

/// <summary>
/// Reads all game entities and caches them as an atomic snapshot.
/// Called from a dedicated high-priority thread.
/// NO team filtering — features (ESP/aimbot) apply their own filters.
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
            var vm = _mem.Read<ViewMatrix>(_mem.ClientAddr(Offsets.dwViewMatrix));
            Matrix = vm;

            long localPawn = _mem.Read<long>(_mem.ClientAddr(Offsets.dwLocalPlayerPawn));
            LocalPawn = localPawn;

            if (localPawn == 0) { InGame = false; return; }
            InGame = true;

            int localTeam = _mem.ReadByte(localPawn + Offsets.m_iTeamNum);
            LocalTeam = localTeam;
            LocalPosition = _mem.Read<Vector3>(localPawn + Offsets.m_vOldOrigin);

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

                var feetPos = _mem.Read<Vector3>(pawn + Offsets.m_vOldOrigin);
                if (feetPos.IsZero) continue;

                var headPos = new Vector3(feetPos.X, feetPos.Y, feetPos.Z + Offsets.PLAYER_HEIGHT);

                bool feetOk = vm.WorldToScreen(feetPos, out var sf, screenW, screenH);
                bool headOk = vm.WorldToScreen(headPos, out var sh, screenW, screenH);
                bool onScreen = feetOk && headOk;

                // Read player name
                string name = "";
                try { name = _mem.ReadString(controller + Offsets.m_iszPlayerName, 32); }
                catch { /* controller might be transitioning */ }

                // ── Bone reading ──
                var boneWorld  = new Vector3[PlayerData.MAX_BONES];
                var boneScreen = new Vector3[PlayerData.MAX_BONES];
                var boneValid  = new bool[PlayerData.MAX_BONES];

                long gsn = _mem.Read<long>(pawn + Offsets.m_pGameSceneNode);
                if (gsn != 0)
                {
                    long boneArray = _mem.Read<long>(gsn + Offsets.m_modelState + Offsets.BONE_ARRAY_OFFSET);
                    if (boneArray > 0x10000) // Sanity: must be a valid heap pointer
                    {
                        // Read all bones used in connections
                        foreach (var (from, to) in Offsets.BoneConnections)
                        {
                            TryReadBone(vm, boneArray, from, boneWorld, boneScreen, boneValid,
                                        screenW, screenH, feetPos);
                            TryReadBone(vm, boneArray, to, boneWorld, boneScreen, boneValid,
                                        screenW, screenH, feetPos);
                        }

                        // If we got the head bone, use it for a more accurate headPos
                        if (boneValid[Offsets.BONE_HEAD])
                        {
                            headPos = boneWorld[Offsets.BONE_HEAD];
                            if (vm.WorldToScreen(headPos, out var headScr, screenW, screenH))
                                sh = headScr;
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

    /// <summary>
    /// Reads a single bone position from the bone array.
    /// Validates that the position is spatially close to the player (rejects garbage data).
    /// Stores both world and screen positions.
    /// </summary>
    private void TryReadBone(ViewMatrix vm, long boneArray, int boneId,
        Vector3[] boneWorld, Vector3[] boneScreen, bool[] boneValid,
        int screenW, int screenH, Vector3 feetPos)
    {
        if (boneId < 0 || boneId >= PlayerData.MAX_BONES) return;
        if (boneValid[boneId]) return; // Already read

        long addr = boneArray + boneId * Offsets.BONE_STRIDE;
        var world = _mem.Read<Vector3>(addr);

        // Reject zero or NaN
        if (world.IsZero) return;
        if (float.IsNaN(world.X) || float.IsNaN(world.Y) || float.IsNaN(world.Z)) return;

        // Spatial validation: bone must be within 120 units of player feet
        float dx = world.X - feetPos.X;
        float dy = world.Y - feetPos.Y;
        float dz = world.Z - feetPos.Z;
        float distSq = dx * dx + dy * dy + dz * dz;
        if (distSq > 120f * 120f) return;

        boneWorld[boneId] = world;

        if (vm.WorldToScreen(world, out var screen, screenW, screenH))
        {
            boneScreen[boneId] = screen;
            boneValid[boneId] = true;
        }
    }
}

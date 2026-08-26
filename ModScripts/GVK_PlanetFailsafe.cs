using System;
using System.Collections.Generic;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;

namespace GVK.PlanetFailsafe
{
    /// <summary>
    /// Monitors the core regions of planets and safely rescues dynamic grids and player characters
    /// that have clipped or fallen through voxel terrain, while removing fallen floating items to reduce server load.
    /// </summary>
    [MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
    public class PlanetFailsafeSession : MySessionComponentBase
    {
        private const int SCAN_INTERVAL_TICKS = 300;     // Scan every 5 seconds (300 ticks)
        private const int IMMUNITY_DURATION_TICKS = 180; // 3 seconds of spawn collision protection
        private const int MIN_GRID_BLOCK_COUNT = 5;      // Ignore tiny debris chunks

        private int ticks = 0;
        private bool isServer;

        private readonly List<MyPlanet> planets = new List<MyPlanet>();
        private readonly List<BoundingSphereD> rescueSpheres = new List<BoundingSphereD>();
        private readonly List<MyEntity> dynamicEntitiesBuffer = new List<MyEntity>();
        private readonly Dictionary<MyCubeGrid, int> protectedGrids = new Dictionary<MyCubeGrid, int>();

        public override void LoadData()
        {
            isServer = MyAPIGateway.Session.IsServer;
            if (!isServer) return;

            MyAPIGateway.Entities.OnEntityAdd += OnEntityAdded;
        }

        public override void BeforeStart()
        {
            if (!isServer) return;

            // Catch any planets already loaded into the world
            HashSet<IMyEntity> entities = new HashSet<IMyEntity>();
            MyAPIGateway.Entities.GetEntities(entities, e => e is MyPlanet);
            foreach (var ent in entities)
            {
                var planet = ent as MyPlanet;
                if (planet != null)
                {
                    RegisterPlanet(planet);
                }
            }
        }

        private void OnEntityAdded(IMyEntity entity)
        {
            var planet = entity as MyPlanet;
            if (planet != null)
            {
                RegisterPlanet(planet);
            }
        }

        private void RegisterPlanet(MyPlanet planet)
        {
            if (planets.Contains(planet)) return;

            planets.Add(planet);
            // Rescue zone is everything inside the planet's lowest minimum radius minus 100m
            double detectionRadius = Math.Max(100.0, planet.MinimumRadius - 100.0);
            rescueSpheres.Add(new BoundingSphereD(planet.PositionComp.GetPosition(), detectionRadius));
        }

        public override void UpdateAfterSimulation()
        {
            if (!isServer) return;

            ticks++;

            // Manage temporary damage immunity countdowns
            if (protectedGrids.Count > 0)
            {
                UpdateProtectedGrids();
            }

            if (ticks % SCAN_INTERVAL_TICKS == 0)
            {
                ScanAndRescue();
            }
        }

        private void ScanAndRescue()
        {
            for (int i = 0; i < rescueSpheres.Count; i++)
            {
                BoundingSphereD sphere = rescueSpheres[i];
                dynamicEntitiesBuffer.Clear();

                MyGamePruningStructure.GetAllTopMostEntitiesInSphere(ref sphere, dynamicEntitiesBuffer, MyEntityQueryType.Dynamic);

                for (int j = 0; j < dynamicEntitiesBuffer.Count; j++)
                {
                    MyEntity ent = dynamicEntitiesBuffer[j];
                    if (ent == null || ent.MarkedForClose || ent.Closed) continue;

                    // 1. Delete fallen floating items / ores to prevent server core lag
                    if (ent is IMyFloatingObject)
                    {
                        ent.Close();
                        continue;
                    }

                    // 2. Rescue fallen characters
                    var character = ent as IMyCharacter;
                    if (character != null)
                    {
                        RescueCharacter(character);
                        continue;
                    }

                    // 3. Rescue fallen grids
                    var cubeGrid = ent as MyCubeGrid;
                    if (cubeGrid != null)
                    {
                        RescueGrid(cubeGrid);
                        continue;
                    }
                }
            }
        }

        private void RescueCharacter(IMyCharacter character)
        {
            // Do not reposition seated or dead characters
            if (character.IsDead || character.Parent != null) return;
            if (character.ControllerInfo?.ControllingIdentityId == 0) return;

            Vector3D charPos = character.PositionComp.GetPosition();
            MyPlanet planet = MyGamePruningStructure.GetClosestPlanet(charPos);
            if (planet == null) return;

            Vector3D surfacePoint = planet.GetClosestSurfacePointGlobal(ref charPos);
            Vector3D upVec = surfacePoint - planet.PositionComp.GetPosition();
            if (upVec.LengthSquared() < 1.0) upVec = Vector3D.Up;
            else upVec.Normalize();

            Vector3D targetPos = surfacePoint + (upVec * 3.0);

            // Zero terminal fall speed to prevent instant impact death on arrival
            character.Physics?.ClearSpeed();
            character.SetPosition(targetPos);
            character.Physics?.ClearSpeed();
        }

        private void RescueGrid(MyCubeGrid grid)
        {
            if (grid.IsStatic || grid.BlocksCount < MIN_GRID_BLOCK_COUNT) return;

            Vector3D gridPos = grid.PositionComp.GetPosition();
            MyPlanet planet = MyGamePruningStructure.GetClosestPlanet(gridPos);
            if (planet == null) return;

            Vector3D surfacePoint = planet.GetClosestSurfacePointGlobal(ref gridPos);
            Vector3D upVec = surfacePoint - planet.PositionComp.GetPosition();
            if (upVec.LengthSquared() < 1.0) upVec = Vector3D.Up;
            else upVec.Normalize();

            BoundingSphereD gridSphere = grid.PositionComp.WorldVolume;
            Vector3D targetPos = surfacePoint + (upVec * (gridSphere.Radius + 3.0));

            // Construct orthogonal world matrix aligned upright with planet surface normal
            Vector3D forward = Vector3D.CalculatePerpendicularVector(upVec);
            MatrixD worldMatrix = MatrixD.CreateWorld(targetPos, forward, upVec);

            // Lock handbrakes on all cockpits to prevent runaway rovers on slopes
            var fatBlocks = grid.GetFatBlocks();
            foreach (var block in fatBlocks)
            {
                var cockpit = block as IMyCockpit;
                if (cockpit != null)
                {
                    cockpit.HandBrake = true;
                }
            }

            // Zero velocity across root grid and all attached subgrids (wheels, rotors, pistons)
            ClearGridGroupSpeed(grid);
            grid.Teleport(worldMatrix, null, true);
            ClearGridGroupSpeed(grid);

            // Grant brief temporary immunity (3s) to prevent impact destruction during placement
            if (!protectedGrids.ContainsKey(grid))
            {
                if (grid.DestructibleBlocks)
                {
                    grid.DestructibleBlocks = false;
                    protectedGrids.Add(grid, IMMUNITY_DURATION_TICKS);
                }
            }

            MyLog.Default.WriteLine($"[GVK PlanetFailsafe] Rescued grid '{grid.DisplayName}' to surface at {targetPos:0}");
        }

        private void ClearGridGroupSpeed(IMyCubeGrid rootGrid)
        {
            var groupGrids = new List<IMyCubeGrid>();
            MyAPIGateway.GridGroups.GetGridGroup(GridLinkTypeEnum.Mechanical, rootGrid)?.GetGrids(groupGrids);

            if (groupGrids.Count == 0)
            {
                rootGrid.Physics?.ClearSpeed();
                return;
            }

            for (int i = 0; i < groupGrids.Count; i++)
            {
                groupGrids[i].Physics?.ClearSpeed();
            }
        }

        private void UpdateProtectedGrids()
        {
            List<MyCubeGrid> expired = null;

            var keys = new List<MyCubeGrid>(protectedGrids.Keys);
            foreach (var grid in keys)
            {
                if (grid == null || grid.MarkedForClose || grid.Closed)
                {
                    if (expired == null) expired = new List<MyCubeGrid>();
                    expired.Add(grid);
                    continue;
                }

                int remaining = protectedGrids[grid] - 1;
                if (remaining <= 0)
                {
                    grid.DestructibleBlocks = true;
                    if (expired == null) expired = new List<MyCubeGrid>();
                    expired.Add(grid);
                }
                else
                {
                    protectedGrids[grid] = remaining;
                }
            }

            if (expired != null)
            {
                for (int i = 0; i < expired.Count; i++)
                {
                    protectedGrids.Remove(expired[i]);
                }
            }
        }

        protected override void UnloadData()
        {
            if (isServer)
            {
                MyAPIGateway.Entities.OnEntityAdd -= OnEntityAdded;
            }

            planets.Clear();
            rescueSpheres.Clear();
            dynamicEntitiesBuffer.Clear();

            // Restore destructible state on any remaining protected grids
            foreach (var kvp in protectedGrids)
            {
                if (kvp.Key != null && !kvp.Key.Closed)
                {
                    kvp.Key.DestructibleBlocks = true;
                }
            }
            protectedGrids.Clear();
        }
    }
}

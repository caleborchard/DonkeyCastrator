using System;
using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;
using UnityEngine;
using UnityEngine.SceneManagement;

[assembly: MelonInfo(typeof(DonkeyCastrator.Core), "DonkeyCastrator", "1.0.0", "Caleb Orchard", null)]
[assembly: MelonGame("DefaultCompany", "BabySteps")]

namespace DonkeyCastrator
{
    public class Core : MelonMod
    {
        private readonly HashSet<int> reportedInstanceIds = new();
        private readonly HashSet<int> scannedIds = new();
        private readonly Dictionary<int, int> childCountCache = new();

        private readonly HashSet<int> discoveredNodeIds = new();
        private readonly Dictionary<int, int> discoveryChildCountCache = new();
        private readonly Queue<Transform> discoveryQueue = new();
        private readonly HashSet<int> queuedDiscoveryIds = new();

        private readonly List<Transform> censorRoots = new();
        private readonly HashSet<int> censorRootIds = new();
        private readonly List<Transform> censoredTargets = new();
        private readonly HashSet<int> censoredTargetIds = new();

        private float nextScanTime;
        private float nextDiscoveryTime;
        private float nextWarningTime;

        private const float ScanInterval = 1f;
        private const float DiscoveryInterval = 5f;
        private const float WarningCooldown = 2f;
        private const float DiscoveryFrameBudgetMs = 1.0f;
        private const int MaxDiscoveryNodesPerFrame = 200;

        public override void OnInitializeMelon()
        {
            LoggerInstance.Msg("Initialized.");
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            DiscoverCensorRoots(force: true);
        }

        public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
        {
            reportedInstanceIds.Clear();
            scannedIds.Clear();
            childCountCache.Clear();

            discoveredNodeIds.Clear();
            discoveryChildCountCache.Clear();
            discoveryQueue.Clear();
            queuedDiscoveryIds.Clear();

            censorRoots.Clear();
            censorRootIds.Clear();
            censoredTargets.Clear();
            censoredTargetIds.Clear();
        }

        public override void OnUpdate()
        {
            EnforceCensoredTargets();

            float now = Time.unscaledTime;

            if (now >= nextDiscoveryTime)
            {
                nextDiscoveryTime = now + DiscoveryInterval;
                DiscoverCensorRoots(force: false);
            }

            ProcessDiscoveryQueue();

            if (now < nextScanTime) return;
            nextScanTime = now + ScanInterval;

            ScanKnownRoots();
        }

        public override void OnLateUpdate()
        {
            EnforceCensoredTargets();
        }

        private void ScanKnownRoots()
        {
            try
            {
                List<string> found = new();

                for (int i = censorRoots.Count - 1; i >= 0; i--)
                {
                    Transform root = censorRoots[i];
                    if (!IsLoaded(root))
                    {
                        censorRoots.RemoveAt(i);
                        continue;
                    }

                    CheckObjectRecursive(root, found);
                }

                if (found.Count > 0)
                {
                    LoggerInstance.Msg($"Found {found.Count} flagged object(s):");
                    for (int i = 0; i < found.Count; i++) LoggerInstance.Msg($"  - {found[i]}");
                }
            }
            catch (Exception ex)
            {
                if (Time.unscaledTime >= nextWarningTime)
                {
                    MelonLogger.Warning($"DonkeyCastrator scan error: {ex.Message}");
                    nextWarningTime = Time.unscaledTime + WarningCooldown;
                }
            }
        }

        private void DiscoverCensorRoots(bool force)
        {
            if (force)
            {
                discoveredNodeIds.Clear();
                discoveryChildCountCache.Clear();
                discoveryQueue.Clear();
                queuedDiscoveryIds.Clear();

                censorRoots.Clear();
                censorRootIds.Clear();
            }

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;

                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    if (root == null) continue;
                    EnqueueForDiscovery(root.transform);
                }
            }
        }

        private void ProcessDiscoveryQueue()
        {
            if (discoveryQueue.Count == 0) return;

            float start = Time.realtimeSinceStartup;
            int processed = 0;

            while (discoveryQueue.Count > 0 && processed < MaxDiscoveryNodesPerFrame)
            {
                if ((Time.realtimeSinceStartup - start) * 1000f >= DiscoveryFrameBudgetMs) break;

                Transform t = discoveryQueue.Dequeue();
                if (t != null) queuedDiscoveryIds.Remove(t.GetInstanceID());

                processed++;
                DiscoverCensorRootsStep(t);
            }
        }

        private void DiscoverCensorRootsStep(Transform t)
        {
            if (t == null) return;

            int id = t.GetInstanceID();
            int childCount = t.childCount;

            if (discoveredNodeIds.Contains(id) && discoveryChildCountCache.TryGetValue(id, out int cached) && cached == childCount) return;

            if (t.GetComponent<RegisterCensorBones>() != null && censorRootIds.Add(id))
            {
                censorRoots.Add(t);
                LoggerInstance.Msg($"Found RegisterCensorBones on parent object: {t.gameObject.name}");
            }

            for (int i = 0; i < childCount; i++) EnqueueForDiscovery(t.GetChild(i));

            discoveredNodeIds.Add(id);
            discoveryChildCountCache[id] = childCount;
        }

        private void EnqueueForDiscovery(Transform t)
        {
            if (t == null) return;

            int id = t.GetInstanceID();
            if (!queuedDiscoveryIds.Add(id)) return;
            discoveryQueue.Enqueue(t);
        }

        private void CheckObjectRecursive(Transform t, List<string> found)
        {
            if (t == null) return;

            int id = t.GetInstanceID();
            int childCount = t.childCount;

            if (reportedInstanceIds.Contains(id))
            {
                EnsureZeroScale(t);
                CacheScanState(id, childCount);
                return;
            }

            if (scannedIds.Contains(id) && childCountCache.TryGetValue(id, out int cached) && cached == childCount) return;

            string name = t.gameObject.name;
            bool isTarget =
                name.IndexOf("penis", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("testes", StringComparison.OrdinalIgnoreCase) >= 0;

            if (isTarget)
            {
                bool isExcluded =
                    name.IndexOf("Boing", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Censor", StringComparison.OrdinalIgnoreCase) >= 0;

                if (!isExcluded)
                {
                    reportedInstanceIds.Add(id);
                    found.Add(name);
                    if (censoredTargetIds.Add(id)) censoredTargets.Add(t);
                    EnsureZeroScale(t);
                }

                CacheScanState(id, childCount);
                return;
            }

            for (int i = 0; i < childCount; i++) CheckObjectRecursive(t.GetChild(i), found);

            CacheScanState(id, childCount);
        }

        private void EnforceCensoredTargets()
        {
            for (int i = censoredTargets.Count - 1; i >= 0; i--)
            {
                Transform target = censoredTargets[i];
                if (!IsLoaded(target))
                {
                    censoredTargets.RemoveAt(i);
                    continue;
                }

                EnsureZeroScale(target);
            }
        }

        private bool IsLoaded(Transform t)
        {
            return t != null && t.gameObject.scene.isLoaded;
        }

        private void EnsureZeroScale(Transform t)
        {
            if (t.localScale != Vector3.zero) t.localScale = Vector3.zero;
        }

        private void CacheScanState(int id, int childCount)
        {
            scannedIds.Add(id);
            childCountCache[id] = childCount;
        }
    }
}
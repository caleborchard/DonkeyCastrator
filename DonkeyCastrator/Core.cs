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
        private readonly HashSet<int> reportedInstanceIds = new HashSet<int>();
        private readonly HashSet<int> scannedIds = new HashSet<int>();
        private readonly Dictionary<int, int> childCountCache = new Dictionary<int, int>();

        private readonly HashSet<int> discoveredNodeIds = new HashSet<int>();
        private readonly Dictionary<int, int> discoveryChildCountCache = new Dictionary<int, int>();
        private readonly Queue<Transform> discoveryQueue = new Queue<Transform>();
        private readonly HashSet<int> queuedDiscoveryIds = new HashSet<int>();

        private readonly List<Transform> censorRoots = new List<Transform>();
        private readonly HashSet<int> censorRootIds = new HashSet<int>();
        private readonly List<Transform> censoredTargets = new List<Transform>();
        private readonly HashSet<int> censoredTargetIds = new HashSet<int>();

        private float nextScanTime = 0f;
        private float nextDiscoveryTime = 0f;
        private float nextWarningTime = 0f;

        private const float ScanInterval = 1f;
        private const float DiscoveryInterval = 5f;
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
                List<string> found = new List<string>();

                for (int i = censorRoots.Count - 1; i >= 0; i--)
                {
                    Transform root = censorRoots[i];
                    if (root == null || !root.gameObject.scene.isLoaded)
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
                    nextWarningTime = Time.unscaledTime + 2f;
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
                if (t.localScale != Vector3.zero) t.localScale = Vector3.zero;
                scannedIds.Add(id);
                childCountCache[id] = childCount;
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
                    if (t.localScale != Vector3.zero) t.localScale = Vector3.zero;
                }

                scannedIds.Add(id);
                childCountCache[id] = childCount;
                return;
            }

            for (int i = 0; i < childCount; i++) CheckObjectRecursive(t.GetChild(i), found);

            scannedIds.Add(id);
            childCountCache[id] = childCount;
        }

        private void EnforceCensoredTargets()
        {
            for (int i = censoredTargets.Count - 1; i >= 0; i--)
            {
                Transform target = censoredTargets[i];
                if (target == null || !target.gameObject.scene.isLoaded)
                {
                    censoredTargets.RemoveAt(i);
                    continue;
                }

                if (target.localScale != Vector3.zero) target.localScale = Vector3.zero;
            }
        }
    }
}
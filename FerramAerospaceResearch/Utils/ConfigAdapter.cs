using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FerramAerospaceResearch.Interfaces;
using FerramAerospaceResearch.Reflection;
using UnityEngine;

namespace FerramAerospaceResearch
{
    [FARAddon(700, true)]
    public class ConfigAdapter : MonoSingleton<ConfigAdapter>, IWaitForAddon, IReloadable
    {
        private enum Operation
        {
            None,
            Loading,
            Saving
        }

        private struct LoadGuard : IDisposable
        {
            public Operation operation;
            // cannot override default ctor...
            public LoadGuard(Operation op)
            {
                operation = op;
                switch (op)
                {
                    case Operation.Loading:
                        FARLogger.Debug("ConfigAdapter started loading");
                        FARConfig.IsLoading = true;
                        Instance.loading = true;
                        break;
                    case Operation.Saving:
                        FARLogger.Debug("ConfigAdapter started saving");
                        Instance.saving = true;
                        break;
                    case Operation.None:
                        FARLogger.Debug("ConfigAdapter started task");
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(op), op, null);
                }
            }

            public void Dispose()
            {
                switch (operation)
                {
                    case Operation.Loading:
                        FARLogger.Debug("ConfigAdapter finished loading");
                        FARConfig.IsLoading = false;
                        Instance.loading = false;
                        break;
                    case Operation.Saving:
                        FARLogger.Debug("ConfigAdapter finished saving");
                        Instance.saving = false;
                        break;
                    case Operation.None:
                        FARLogger.Debug("ConfigAdapter finished task");
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
                }

                Instance.Completed = !(Instance.saving || Instance.loading);
            }
        }

        private static LoadGuard Guard(Operation op)
        {
            return new LoadGuard(op);
        }

        private static readonly Dictionary<string, string> lastConfigs = new Dictionary<string, string>();
        public int Priority { get; set; } = 999;

        public void DoReload()
        {
            StartCoroutine(DoLoadConfig());
        }

        public bool Completed { get; set; }

        private volatile bool loading;
        private volatile bool saving;

        private void Start()
        {
            StartCoroutine(DoSetup());
        }

        private static IEnumerator DoSetup()
        {
            using LoadGuard guard = Guard(Operation.None);

            Task task = Task.Factory.StartNew(() => ConfigReflection.Instance.Initialize());

            while (!task.IsCompleted)
                yield return null;

            if (task.Exception != null)
                FARLogger.Exception(task.Exception, "Exception while setting up config");
        }

        private static void LoadConfigs()
        {
            // clear config cache so it can be rebuilt
            lastConfigs.Clear();

            var load = new LoadVisitor();
            foreach (KeyValuePair<string, ReflectedConfig> pair in ConfigReflection.Instance.Configs)
            {
                ConfigNode[] nodes = GameDatabase.Instance.GetConfigNodes(pair.Key);
                if (pair.Value.Reflection.AllowMultiple)
                {
                    var root = new ConfigNode();
                    foreach (ConfigNode configNode in nodes)
                        root.AddNode(configNode);

                    load.Node = root;
                    object instance = pair.Value.Instance;
                    int errors = pair.Value.Reflection.Load(load, ref instance);

                    if (errors > 0)
                        FARLogger.ErrorFormat("{0} errors while loading {1}", errors.ToString(), pair.Key);
                }
                else
                {
                    if (nodes.Length == 0)
                    {
                        FARLogger.Warning($"Could not find config nodes {pair.Key}");
                        continue;
                    }

                    if (nodes.Length > 1)
                        FARLogger.Warning($"Found {nodes.Length.ToString()} {pair.Key} nodes");

                    foreach (ConfigNode node in nodes)
                    {
                        load.Node = node;
                        object instance = pair.Value.Instance;
                        int errors = pair.Value.Reflection.Load(load, ref instance);

                        if (errors > 0)
                            FARLogger.ErrorFormat("{0} errors while loading {1}", errors.ToString(), node.name);
                    }
                }
            }

            using LoadGuard guard = Guard(Operation.Saving);
            SaveConfigs("Custom", true, ".cfg.far", FARConfig.Debug.DumpOnLoad);
        }

        public static IEnumerator SaveAll()
        {
            if (Instance.saving)
                yield break;

            while (Instance.loading)
                yield return null;

            using LoadGuard guard = Guard(Operation.Saving);

            Task task = Task.Factory.StartNew(Save);
            yield return new WaitUntil(() => task.IsCompleted);

            if (task.Exception != null)
                FARLogger.Exception(task.Exception, "Exception while saving up configs");
        }

        public static void Save()
        {
            using LoadGuard guard = Guard(Operation.Saving);
            SaveConfigs("Custom");
        }

        private static void SaveConfigs(
            string prefix,
            bool isPatch = true,
            string extension = ".cfg",
            bool force = false
        )
        {
            var topNode = new ConfigNode();
            var node = new ConfigNode();
            var save = new SaveVisitor
            {
                IsPatch = isPatch,
                Node = node
            };
            foreach (KeyValuePair<string, ReflectedConfig> pair in ConfigReflection.Instance.Configs)
            {
                if (!pair.Value.Reflection.ShouldSave)
                    continue;

                pair.Value.Reflection.Save(save, pair.Value.Instance);

                if (!save.Node.HasData)
                    continue;

                string path = PathUtil.Combine(KSPUtils.GameDataPath, $"{prefix}_{pair.Key}{extension}");
                string nodeStr;

                if (!pair.Value.Reflection.AllowMultiple)
                {
                    Serialization.MakeTopNode(topNode, node, pair.Key, null, isPatch);
                    nodeStr = topNode.ToString();
                }
                else
                {
                    nodeStr = node.ToString();
                }

                if (lastConfigs.TryGetValue(pair.Key, out string oldStr))
                    lastConfigs[pair.Key] = nodeStr;
                else
                    lastConfigs.Add(pair.Key, nodeStr);

                // only write if requested or if the node has been modified, first time oldStr should be null so can be skipped
                if (force || (!string.IsNullOrEmpty(oldStr) && nodeStr != oldStr))
                {
                    FARLogger.DebugFormat("Saving {0} config to {1}", pair.Key, path);
                    System.IO.File.WriteAllText(path, nodeStr);
                }
                else
                {
                    FARLogger.DebugFormat("{0} does not require saving", pair.Key);
                }

                topNode.ClearData();
                node.ClearData();
            }
        }

        private IEnumerator DoLoadConfig()
        {
            if (loading)
                yield break;

            while (saving)
                yield return null;

            using LoadGuard guard = Guard(Operation.Loading);
            Task task = Task.Factory.StartNew(LoadConfigs);

            yield return new WaitWhile(() => !task.IsCompleted);

            if (task.Exception != null)
                FARLogger.Exception(task.Exception, "Exception while loading config");
        }

        protected override void OnDestruct()
        {
            while (loading || saving)
                Thread.Sleep(1);
        }
    }

    public class SaveVisitor : INodeSaver
    {
        public ConfigNode Node { get; set; }
        public bool IsPatch { get; set; }

        /// <inheritdoc />
        public bool OnValue(object value, ValueReflection reflection, out object newValue)
        {
            newValue = default;
            if (value != null)
                Serialization.AddValue(Node, reflection.Name, value, reflection.ValueType, IsPatch);

            return false;
        }

        /// <inheritdoc />
        public bool OnListValue(int index, object value, ListValueReflection reflection, out object newValue)
        {
            if (index == 0 && IsPatch)
                Node.AddValue($"!{reflection.Name}", "deleted");

            newValue = default;
            if (value != null)
                Serialization.AddValue(Node, reflection.Name, value, reflection.ValueType, false, index);

            return false;
        }

        /// <inheritdoc />
        public bool OnNode(object value, NodeReflection reflection, out object newValue)
        {
            newValue = default;
            var save = new SaveVisitor
            {
                IsPatch = IsPatch,
                Node = new ConfigNode()
            };
            reflection.Save(save, value);

            if (save.Node.HasData)
                Serialization.AddNode(Node, save.Node, reflection.Id, reflection.Name, IsPatch);

            return false;
        }

        /// <inheritdoc />
        public bool OnListNode(
            int index,
            object value,
            ListValueReflection reflection,
            NodeReflection nodeReflection,
            out object newValue
        )
        {
            if (index == 0 && IsPatch)
            {
                Node.AddNode(string.IsNullOrEmpty(reflection.Name)
                                 ? $"!{reflection.NodeId},*"
                                 : $"!{reflection.NodeId}[{reflection.Name}],*");
            }

            newValue = default;
            var save = new SaveVisitor
            {
                IsPatch = false,
                Node = new ConfigNode()
            };
            nodeReflection.Save(save, value);

            if (save.Node.HasData)
                Serialization.AddNode(Node, save.Node, nodeReflection.Id, reflection.Name);

            return false;
        }
    }

    public class LoadVisitor : INodeLoader
    {
        public ConfigNode Node { get; set; }

        /// <inheritdoc />
        public bool OnValue(ValueReflection reflection, out object newValue)
        {
            if (!Serialization.TryGetValue(Node, reflection.Name, out newValue, reflection.ValueType))
                return false;

            FARLogger.DebugFormat("Parsed {0}.{1} = {2}", Node.name, reflection.Name, newValue);
            return true;
        }

        /// <inheritdoc />
        public bool OnValueList(ListValueReflection reflection, out IList newValue)
        {
            List<string> values = Node.GetValuesList(reflection.Name);
            newValue = default;
            if (values.Count == 0)
                return false;

            newValue = new List<object>();
            foreach (string str in values)
            {
                if (!Serialization.TryGetValue(str, out object v, reflection.ValueType))
                    continue;

                newValue.Add(v);
            }

            FARLogger.DebugFormat("Parsed {0}.{1} with {2} values", Node.name, reflection.Name, newValue.Count);

            return true;
        }

        /// <inheritdoc />
        public bool OnNode(object nodeObject, NodeReflection reflection, out object newValue)
        {
            ConfigNode node = string.IsNullOrEmpty(reflection.Name)
                                  ? Node.GetNode(reflection.Id)
                                  : Node.GetNode(reflection.Id, "name", reflection.Name);
            if (node != null)
            {
                var load = new LoadVisitor {Node = node};
                int errors = reflection.Load(load, ref nodeObject);

                if (errors > 0)
                    FARLogger.ErrorFormat("{0} errors while loading {1}[{2}]",
                                          errors.ToString(),
                                          node.name,
                                          reflection.Name);
            }

            newValue = nodeObject;
            return true;
        }

        /// <inheritdoc />
        public bool OnNodeList(ListValueReflection reflection, NodeReflection nodeReflection, out IList newValue)
        {
            ConfigNode[] nodes = Node.GetNodes(reflection.NodeId);
            var load = new LoadVisitor();

            int index = 0;
            int i = 0;

            var items = new List<object>(nodes.Length);

            foreach (ConfigNode node in nodes)
            {
                if (!node.TryGetValue("index", ref i))
                    i = index;
                index++;

                load.Node = node;
                object item = nodeReflection.Load(load, out int errors);
                SetItem(items, item, i);

                if (errors > 0)
                    FARLogger.ErrorFormat("{0} errors while loading {1}[{2}]",
                                          errors.ToString(),
                                          node.name,
                                          i.ToString());
            }

            newValue = items;
            return true;
        }

        private static void SetItem(IList list, object item, int index)
        {
            for (int i = list.Count; i <= index; i++)
                list.Add(default);

            list[index] = item;
        }
    }
}

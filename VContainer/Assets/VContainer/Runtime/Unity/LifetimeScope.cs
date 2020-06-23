using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using VContainer.Internal;

namespace VContainer.Unity
{
    public sealed class LifetimeScope : MonoBehaviour
    {
        public const string AutoReferenceKey = "__auto__";

        [SerializeField]
        MonoInstaller[] monoInstallers;

        [SerializeField]
        ScriptableObjectInstaller[] scriptableObjectInstallers;

        [SerializeField]
        string key = "";

        [SerializeField]
        public LifetimeScope parent;

        [SerializeField]
        public string parentKey;

        [SerializeField]
        bool buildOnAwake = true;

        public const string ProjectRootResourcePath = "ProjectLifetimeScope";

        public static LifetimeScope ProjectRoot => ProjectRootLazy.Value;

        static readonly Lazy<LifetimeScope> ProjectRootLazy = new Lazy<LifetimeScope>(LoadProjectRoot);
        static readonly ConcurrentDictionary<string, ExtraInstaller> ExtraInstallers = new ConcurrentDictionary<string, ExtraInstaller>();
        static readonly List<GameObject> GameObjectBuffer = new List<GameObject>(32);

        public static ExtendScope Push(Action<UnityContainerBuilder> installing, string key = "")
        {
            return new ExtendScope(new ActionInstaller(installing), key);
        }

        public static ExtendScope Push(IInstaller installer, string key = "")
        {
            return new ExtendScope(installer, key);
        }

        public static LifetimeScope FindByKey(string key)
        {
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded)
                {
                    scene.GetRootGameObjects(GameObjectBuffer);
                    foreach (var root in GameObjectBuffer)
                    {
                        var others = root.GetComponentsInChildren<LifetimeScope>();
                        foreach (var other in others)
                        {
                            if (key == other.key)
                            {
                                return other;
                            }
                        }
                    }
                }
            }
            return null;
        }

        public static void EnqueueExtra(IInstaller installer, string key = "")
        {
            ExtraInstallers.AddOrUpdate(key,
                new ExtraInstaller { installer },
                (_, binding) =>
                {
                    binding.Add(installer);
                    return binding;
                });
        }

        public static void RemoveExtra(string key) => ExtraInstallers.TryRemove(key, out _);

        static LifetimeScope LoadProjectRoot()
        {
            UnityEngine.Debug.Log($"VContainer try to load project root: {ProjectRootResourcePath}");
            var prefabs = Resources.LoadAll(ProjectRootResourcePath, typeof(GameObject));
            if (prefabs.Length > 1)
            {
                throw new VContainerException(null, $"{ProjectRootResourcePath} resource is duplicated!");
            }

            var prefab = (GameObject)prefabs.FirstOrDefault();
            if (prefab == null)
            {
                return null;
            }

            if (prefab.activeSelf)
            {
                prefab.SetActive(false);
            }
            var gameObject = Instantiate(prefab);
            DontDestroyOnLoad(gameObject);
            return gameObject.GetComponent<LifetimeScope>();
        }

        public IObjectResolver Container { get; private set; }
        public string Key => key;

        readonly CompositeDisposable disposable = new CompositeDisposable();
        readonly List<IInstaller> extraInstallers = new List<IInstaller>();

        void Awake()
        {
            if (buildOnAwake)
            {
                Build();
            }
        }

        void OnDestroy()
        {
            disposable.Dispose();
            Container.Dispose();
        }

        public void Build()
        {
            var runtimeParent = GetRuntimeParent();
            if (runtimeParent != null)
            {
                Container = runtimeParent.Container.CreateScope(InstallTo);
            }
            else
            {
                InstallTo(new ContainerBuilder());
            }
            DispatchPlayerLoopItems();
        }

        public GameObject CreateChild(string key = null, Action<UnityContainerBuilder> installation = null)
        {
            if (installation != null)
            {
                return CreateChild(key, new ActionInstaller(installation));
            }
            return CreateChild(key, (IInstaller)null);
        }

        public GameObject CreateChild(string key = "", IInstaller installer = null)
        {
            var childGameObject = new GameObject("LifeTimeScope (Child)");
            childGameObject.SetActive(false);
            childGameObject.transform.SetParent(transform, false);
            var child = childGameObject.AddComponent<LifetimeScope>();
            if (installer != null)
            {
                child.extraInstallers.Add(installer);
            }
            child.parent = this;
            childGameObject.SetActive(true);
            return childGameObject;
        }

        void InstallTo(IContainerBuilder builder)
        {
            var decoratedBuilder = new UnityContainerBuilder(builder, gameObject.scene);
            foreach (var installer in monoInstallers)
            {
                installer.Install(decoratedBuilder);
            }

            foreach (var installer in scriptableObjectInstallers)
            {
                installer.Install(decoratedBuilder);
            }

            foreach (var installer in extraInstallers)
            {
                installer.Install(decoratedBuilder);
            }

            if (ExtraInstallers.TryRemove(key, out var extraInstaller))
            {
                extraInstaller.Install(decoratedBuilder);
            }
            Container = decoratedBuilder.Build();
        }

        LifetimeScope GetRuntimeParent()
        {
            if (parent != null)
                return parent;

            if (!string.IsNullOrEmpty(parentKey) && parentKey != key)
            {
                var found = FindByKey(parentKey);
                if (found != null)
                {
                    return found;
                }
                throw new VContainerException(null, $"LifetimeScope parent key `{parentKey}` is not found in any scene");
            }

            if (ProjectRoot != null && ProjectRoot != this)
            {
                if (!ProjectRoot.gameObject.activeSelf)
                {
                    ProjectRoot.gameObject.SetActive(true);
                }
                return ProjectRoot;
            }
            return null;
        }

        void DispatchPlayerLoopItems()
        {
            try
            {
                var markers = Container.Resolve<IEnumerable<IInitializable>>();
                var loopItem = new InitializationLoopItem(markers);
                disposable.Add(loopItem);
                PlayerLoopHelper.Dispatch(PlayerLoopTiming.Initialization, loopItem);
            }
            catch (VContainerException ex) when(ex.InvalidType == typeof(IEnumerable<IInitializable>))
            {
            }

            try
            {
                var markers = Container.Resolve<IEnumerable<IPostInitializable>>();
                var loopItem = new PostInitializationLoopItem(markers);
                disposable.Add(loopItem);
                PlayerLoopHelper.Dispatch(PlayerLoopTiming.PostInitialization, loopItem);
            }
            catch (VContainerException ex) when(ex.InvalidType == typeof(IEnumerable<IPostInitializable>))
            {
            }

            try
            {
                var markers = Container.Resolve<IEnumerable<IFixedTickable>>();
                var loopItem = new FixedTickableLoopItem(markers);
                disposable.Add(loopItem);
                PlayerLoopHelper.Dispatch(PlayerLoopTiming.FixedUpdate, loopItem);
            }
            catch (VContainerException ex) when(ex.InvalidType == typeof(IEnumerable<IFixedTickable>))
            {
            }

            try
            {
                var markers = Container.Resolve<IEnumerable<IPostFixedTickable>>();
                var loopItem = new PostFixedTickableLoopItem(markers);
                disposable.Add(loopItem);
                PlayerLoopHelper.Dispatch(PlayerLoopTiming.PostFixedUpdate, loopItem);
            }
            catch (VContainerException ex) when(ex.InvalidType == typeof(IEnumerable<IPostFixedTickable>))
            {
            }

            try
            {
                var markers = Container.Resolve<IEnumerable<ITickable>>();
                var loopItem = new TickableLoopItem(markers);
                disposable.Add(loopItem);
                PlayerLoopHelper.Dispatch(PlayerLoopTiming.Update, loopItem);
            }
            catch (VContainerException ex) when(ex.InvalidType == typeof(IEnumerable<ITickable>))
            {
            }

            try
            {
                var markers = Container.Resolve<IEnumerable<IPostTickable>>();
                var loopItem = new PostTickableLoopItem(markers);
                disposable.Add(loopItem);
                PlayerLoopHelper.Dispatch(PlayerLoopTiming.PostUpdate, loopItem);
            }
            catch (VContainerException ex) when(ex.InvalidType == typeof(IEnumerable<IPostTickable>))
            {
            }

            try
            {
                var markers = Container.Resolve<IEnumerable<ILateTickable>>();
                var loopItem = new LateTickableLoopItem(markers);
                disposable.Add(loopItem);
                PlayerLoopHelper.Dispatch(PlayerLoopTiming.LateUpdate, loopItem);
            }
            catch (VContainerException ex) when(ex.InvalidType == typeof(IEnumerable<ILateTickable>))
            {
            }

            try
            {
                var markers = Container.Resolve<IEnumerable<IPostLateTickable>>();
                var loopItem = new PostLateTickableLoopItem(markers);
                disposable.Add(loopItem);
                PlayerLoopHelper.Dispatch(PlayerLoopTiming.PostLateUpdate, loopItem);
            }
            catch (VContainerException ex) when(ex.InvalidType == typeof(IEnumerable<IPostLateTickable>))
            {
            }
        }
    }
}
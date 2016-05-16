﻿using ReactNative.Bridge;
using ReactNative.Bridge.Queue;
using ReactNative.Common;
using ReactNative.Hosting.Bridge;
using ReactNative.Modules.Core;
using ReactNative.Tracing;
using ReactNative.UIManager;
using ReactNative.Views;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI.Xaml;

namespace ReactNative
{
    /// <summary>
    /// This class is managing instances of <see cref="CatalystInstance" />. It expose a way to configure
    /// catalyst instance using <see cref="IReactPackage" /> and keeps track of the lifecycle of that
    /// instance. It also sets up connection between the instance and developers support functionality
    /// of the framework.
    ///
    /// An instance of this manager is required to start JS application in <see cref="ReactRootView" /> (see
    /// <see cref="ReactRootView.StartReactApplication(IReactInstanceManager, string)" /> for more info).
    ///
    /// TODO:
    /// 1.Implement background task functionality and ReactContextInitAsyncTask class hierarchy.
    /// 2.Lifecycle managment functoinality. i.e. resume, pause, etc
    /// 3.Implement Backbutton handler
    /// 4.Implement js bundler load progress checks to ensure thread safety
    /// 5.Implement the ViewGroupManager as well as the main ReactViewManager
    /// 6.Create DevManager functionality to manage things like exceptions.
    /// </summary>
    public class ReactInstanceManagerImpl : IReactInstanceManager
    {
        private readonly List<ReactRootView> _attachedRootViews = new List<ReactRootView>();
        private LifecycleState _lifecycleState;
        private readonly string _jsBundleFile;
        private readonly List<IReactPackage> _packages;
        private ReactContext _reactContext;
        private readonly string _jsMainModuleName;
        private readonly UIImplementationProvider _uiImplementationProvider;
        private readonly IDefaultHardwareBackButtonHandler _defaultHardwareBackButtonHandler;

        public ReactInstanceManagerImpl(
            string jsMainModuleName, 
            List<IReactPackage> packages, 
            LifecycleState initialLifecycleState,
            UIImplementationProvider uiImplementationProvider,
            string jsBundleFile)
        {
            _jsBundleFile = jsBundleFile;
            _jsMainModuleName = jsMainModuleName;
            _packages = packages;
            _reactContext = new ReactApplicationContext();
            _lifecycleState = initialLifecycleState;
            _uiImplementationProvider = uiImplementationProvider;
            _defaultHardwareBackButtonHandler = new DefaultHardwareBackButtonHandlerImpl(this);
        }

        /// <summary>
        /// Uses configured <see cref="IReactPackage" /> instances to create all view managers.
        /// </summary>
        /// <param name="catalystApplicationContext">react application instance</param>
        /// <returns></returns>
        public IReadOnlyList<ViewManager> CreateAllViewManagers(ReactApplicationContext catalystApplicationContext)
        {
            var allViewManagers = new List<ViewManager>();

            foreach (var reactPackage in _packages)
            {
                var viewManagers = reactPackage.CreateViewManagers(catalystApplicationContext);
                allViewManagers.AddRange(viewManagers);
            }

            return allViewManagers;
        }

        /// <summary>
        /// Loads the <see cref="ReactApplicationContext" /> based on the user configured bundle.
        /// </summary>
        public async Task<ReactContext> RecreateReactContextInBackgroundFromBundleFileAsync()
        {
            var jsExecutor = new ChakraJavaScriptExecutor();
            var jsBundler = JavaScriptBundleLoader.CreateFileLoader(_jsBundleFile);

            try
            {
                _reactContext = await CreateReactContextAsync(jsExecutor, jsBundler);
            }
            catch (Exception ex)
            {

                throw ex;
            }

            return _reactContext;
        }

        /// <summary>
        /// Creates the react context instance which is based off the <see cref="CatalystInstance" />.
        /// </summary>
        /// <param name="jsExecutor">The Javascript Executor instance</param>
        /// <param name="jsBundleLoader">The Javascript bundle loader responsible for loading the assembly</param>
        /// <returns>An async task containing the created react context</returns>
        private async Task<ReactContext> CreateReactContextAsync(IJavaScriptExecutor jsExecutor, JavaScriptBundleLoader jsBundleLoader)
        {
            var reactContext = new ReactApplicationContext();
            var nativeRegistryBuilder = new NativeModuleRegistry.Builder();
            var jsModulesBuilder = new JavaScriptModulesConfig.Builder();

            using (Tracer.Trace(Tracer.TRACE_TAG_REACT_BRIDGE, "createAndProcessCoreModulesPackage"))
            {
                var coreModulesPackage = new CoreModulesPackage(
                    this,
                    _defaultHardwareBackButtonHandler,
                    _uiImplementationProvider);

                ProcessPackage(
                    coreModulesPackage,
                    reactContext,
                    nativeRegistryBuilder,
                    jsModulesBuilder);
            }

            foreach (var reactPackage in _packages)
            {
                using (Tracer.Trace(Tracer.TRACE_TAG_REACT_BRIDGE, "createAndProcessCustomReactPackage"))
                {
                    ProcessPackage(
                        reactPackage,
                        reactContext,
                        nativeRegistryBuilder,
                        jsModulesBuilder);
                }
            }

            var nativeModuleRegistry = default(NativeModuleRegistry);
            using (Tracer.Trace(Tracer.TRACE_TAG_REACT_BRIDGE, "buildNativeModuleRegistry"))
            {
                nativeModuleRegistry = nativeRegistryBuilder.Build();
            }

            var javaScriptModulesConfig = default(JavaScriptModulesConfig);
            using (Tracer.Trace(Tracer.TRACE_TAG_REACT_BRIDGE, "buildJSModuleConfig"))
            {
                javaScriptModulesConfig = jsModulesBuilder.Build();
            }

            var exceptionHandler = new Action<Exception>(ex =>
            {
                Tracer.Write(ReactConstants.Tag, String.Format("Exception Occured {0}", ex.Message));
            });
            
            var javascriptRuntime = new CatalystInstance.Builder
            {
                QueueConfigurationSpec = CatalystQueueConfigurationSpec.Default,
                JavaScriptExecutor = jsExecutor,
                Registry = nativeModuleRegistry,
                JavaScriptModulesConfig = javaScriptModulesConfig,
                BundleLoader = jsBundleLoader,
                NativeModuleCallExceptionHandler = exceptionHandler
            }.Build();

            reactContext.InitializeWithInstance(javascriptRuntime);

            await javascriptRuntime.InitializeBridgeAsync();
            
            return reactContext;
        }
        
        private void ProcessPackage(
            IReactPackage reactPackage,
            ReactApplicationContext reactContext,
            NativeModuleRegistry.Builder nativeRegistryBuilder,
            JavaScriptModulesConfig.Builder jsModulesBuilder)
        {
            foreach (var nativeModule in reactPackage.CreateNativeModules(reactContext))
            {
                nativeRegistryBuilder.Add(nativeModule);
            }

            foreach (var type in reactPackage.CreateJavaScriptModulesConfig())
            {
                if (JavaScriptModulesConfig.Builder.ValidJavaScriptModuleType(type))
                {
                    jsModulesBuilder.Add(type);
                } 
            }
        }
        
        /// <summary>
        /// Attaches the <see cref="ReactRootView" /> to the list of tracked root views.
        /// </summary>
        /// <param name="rootView">The root view for the ReactJS app</param>
        public void AttachMeasuredRootView(ReactRootView rootView)
        {
            _attachedRootViews.Add(rootView);
            
            if (_reactContext != null)
            {
                AttachMeasuredRootViewToInstance(rootView, _reactContext.CatalystInstance);
            }
        }

        /// <summary>
        /// Detach given <see cref="rootView" /> from current catalyst instance. 
        /// </summary>
        /// <param name="rootView">The root view for the ReactJS app</param>
        public void DetachRootView(ReactRootView rootView)
        {
            if (_attachedRootViews.RemoveAll(view => view.TagId == rootView.TagId) > -1)
            {
                if (_reactContext != null)
                {
                    DetachViewFromInstance(rootView, _reactContext.CatalystInstance);
                }
            }
        }

        private void DetachViewFromInstance(ReactRootView rootView, ICatalystInstance catalystInstance)
        {
            try
            {
                catalystInstance.GetJavaScriptModule<AppRegistry>()?.unmountApplicationComponentAtRootTag(rootView.TagId);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException("Unable to load AppRegistry JS module. Error message: " + ex.Message, ex);
            }
        }

        private void AttachMeasuredRootViewToInstance(ReactRootView rootView, ICatalystInstance catalystInstance)
        {
            var uiManagerModule = catalystInstance.GetNativeModule<UIManagerModule>();
            var rootTag = uiManagerModule.AddMeasuredRootView(rootView);
            var initialProps = new Dictionary<string, object>();
                initialProps.Add("rootTag", rootTag);

            try
            {
                catalystInstance.GetJavaScriptModule<AppRegistry>()?.runApplication(rootView.JSModuleName, initialProps);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException("Unable to load AppRegistry JS module. Error message: " + ex.Message, ex);
            }
        }

        private void InvokeDefaultOnBackPressed()
        {
            DispatcherHelpers.AssertOnDispatcher();
            // TODO: implement
        }

        class DefaultHardwareBackButtonHandlerImpl : IDefaultHardwareBackButtonHandler
        {
            private readonly ReactInstanceManagerImpl _parent;

            public DefaultHardwareBackButtonHandlerImpl(ReactInstanceManagerImpl parent)
            {
                _parent = parent;
            }

            public void InvokeDefaultOnBackPressed()
            {
                _parent.InvokeDefaultOnBackPressed();
            }
        }

        /// <summary>
        /// A Builder responsible for creating a React Instance Manager.
        /// </summary>
        public sealed class Builder
        {
            private readonly List<IReactPackage> _reactPackages = new List<IReactPackage>();
            private LifecycleState _LifecycleState;
            private UIImplementationProvider _UIImplementationProvider;
            private string _jsBundleFile;
            private string _jsMainModuleName;

            /// <summary>
            /// Sets a provider of <see cref="UIImplementation" />.
            /// </summary>
            public UIImplementationProvider UIImplementationProvider
            {
                set
                {
                    _UIImplementationProvider = value;
                }
            }

            /// <summary>
            /// Path to the JS bundle file to be loaded from the file system.
            /// </summary>
            public string JSBundleFile
            {
                set
                {
                    _jsBundleFile = value;
                }
            }

            /// <summary>
            /// Path to your app's main module on the packager server. This is used when
            /// reloading JS during development. All paths are relative to the root folder
            /// the packager is serving files from.
            /// </summary>
            public string JSMainModuleName
            {
                set
                {
                    _jsMainModuleName = value;
                }
            }

            /// <summary>
            /// Adds a specified <see cref="IReactPackage" /> to the package list.
            /// </summary>
            /// <param name="reactPackage">Client requested package to load.</param>
            /// <returns></returns>
            public Builder AddPackage(IReactPackage reactPackage)
            {
                _reactPackages.Add(reactPackage);
                return this;
            }

            /// <summary>
            /// Concatenates a list of packages to the builder list.
            /// </summary>
            /// <param name="packages">Client requested package list to include</param>
            /// <returns></returns>
            public Builder AddPackages(List<IReactPackage> packages)
            {
                _reactPackages.AddRange(packages);
                return this;
            }

            /// <summary>
            /// Instantiates a new {@link ReactInstanceManagerImpl}.
            /// </summary>
            /// <returns></returns>
            public LifecycleState InitialLifecycleState
            {
                set
                {
                    _LifecycleState = value;
                }
            }

            /// <summary>
            /// Instantiates a new <see cref="ReactInstanceManagerImpl"/> .
            /// Before calling <see mref="build"/>, the following must be called: setApplication then setJSMainModuleName
            /// </summary>
            /// <returns>A IReactInstanceManager instance</returns>
            public IReactInstanceManager Build()
            {
                AssertNotNull(_LifecycleState, nameof(LifecycleState));
                AssertNotNull(_jsMainModuleName, "string");
                AssertNotNull(_jsBundleFile, "string");

                if (_UIImplementationProvider == null)
                {
                    // create default UIImplementationProvider if the provided one is null.
                    _UIImplementationProvider = new UIImplementationProvider();
                }

                return new ReactInstanceManagerImpl(_jsMainModuleName, _reactPackages,
                                                    _LifecycleState, _UIImplementationProvider, _jsBundleFile);
            }

            private void AssertNotNull(object value, string name)
            {
                if (value == null)
                    throw new InvalidOperationException(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "{0} has not been set.",
                            name));
            }
        }
        
        /// <summary>
        /// Dispose the instance of <see cref="IReactInstanceManager"/>, which entails disposing
        /// <see cref="ICatalystInstance"/> and detaching all <see cref="ReactRootView"/> from the instance. 
        /// </summary>
        public void Dispose()
        {
            _reactContext.CatalystInstance.Dispose();

            foreach (var rootView in _attachedRootViews)
            {
                this.DetachRootView(rootView);
            }
        }
    }
}

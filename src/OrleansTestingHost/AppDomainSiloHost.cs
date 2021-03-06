﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using Orleans.CodeGeneration;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Messaging;
using Orleans.Runtime.Placement;
using Orleans.Runtime.TestHooks;
using Orleans.Storage;

namespace Orleans.TestingHost
{
    /// <summary>Allows programmatically hosting an Orleans silo in the curent app domain, exposing some marshable members via remoting.</summary>
    public class AppDomainSiloHost : MarshalByRefObject
    {
        private readonly Silo silo;

        /// <summary>Creates and initializes a silo in the current app domain.</summary>
        /// <param name="name">Name of this silo.</param>
        /// <param name="siloType">Type of this silo.</param>
        /// <param name="config">Silo config data to be used for this silo.</param>
        public AppDomainSiloHost(string name, Silo.SiloType siloType, ClusterConfiguration config)
        {
            this.silo = new Silo(name, siloType, config);
            this.silo.InitializeTestHooksSystemTarget();
            this.AppDomainTestHook = new AppDomainTestHooks(this.silo);
        }

        /// <summary> SiloAddress for this silo. </summary>
        public SiloAddress SiloAddress => silo.SiloAddress;

        /// <summary>Gets the Silo test hook</summary>
        internal ITestHooks TestHook => GrainClient.InternalGrainFactory.GetSystemTarget<ITestHooksSystemTarget>(Constants.TestHooksSystemTargetId, this.SiloAddress);

        internal AppDomainTestHooks AppDomainTestHook { get; }

        /// <summary>Methods for optimizing the code generator.</summary>
        public class CodeGeneratorOptimizer : MarshalByRefObject
        {
            /// <summary>Adds a cached assembly to the code generator.</summary>
            /// <param name="targetAssemblyName">The assembly which the cached assembly was generated for.</param>
            /// <param name="cachedAssembly">The generated assembly.</param>
            public void AddCachedAssembly(string targetAssemblyName, GeneratedAssembly cachedAssembly)
            {
                CodeGeneratorManager.AddGeneratedAssembly(targetAssemblyName, cachedAssembly);
            }
        }

        /// <summary>Represents a collection of generated assemblies accross an application domain.</summary>
        public class GeneratedAssemblies : MarshalByRefObject
        {
            /// <summary>Initializes a new instance of the <see cref="GeneratedAssemblies"/> class.</summary>
            public GeneratedAssemblies()
            {
                Assemblies = new Dictionary<string, GeneratedAssembly>();
            }

            /// <summary>Gets the assemblies which were produced by code generation.</summary>
            public Dictionary<string, GeneratedAssembly> Assemblies { get; }

            /// <summary>Adds a new assembly to this collection.</summary>
            /// <param name="key">The full name of the assembly which code was generated for.</param>
            /// <param name="value">The raw generated assembly.</param>
            public void Add(string key, GeneratedAssembly value)
            {
                if (!string.IsNullOrWhiteSpace(key))
                {
                    Assemblies[key] = value;
                }
            }
        }

        /// <summary>
        /// Populates the provided <paramref name="collection"/> with the assemblies generated by this silo.
        /// </summary>
        /// <param name="collection">The collection to populate.</param>
        public void UpdateGeneratedAssemblies(GeneratedAssemblies collection)
        {
            var generatedAssemblies = CodeGeneratorManager.GetGeneratedAssemblies();
            foreach (var asm in generatedAssemblies)
            {
                collection.Add(asm.Key, asm.Value);
            }
        }

        /// <summary>Starts the silo</summary>
        public void Start()
        {
            silo.Start();
        }

        /// <summary>Gracefully shuts down the silo</summary>
        public void Shutdown()
        {
            silo.Shutdown();
        }
    }

    /// <summary>
    /// Test hook functions for white box testing.
    /// NOTE: this class has to and will be removed entirely. This requires the tests that currently rely on it, to assert using different mechanisms, such as with grains.
    /// </summary>
    internal class AppDomainTestHooks : MarshalByRefObject
    {
        private readonly Silo silo;

        public AppDomainTestHooks(Silo silo)
        {
            this.silo = silo;
        }

        internal IBootstrapProvider GetBootstrapProvider(string name)
        {
            IBootstrapProvider provider = silo.BootstrapProviders.First(p => p.Name.Equals(name));
            return CheckReturnBoundaryReference("bootstrap provider", provider);
        }

        /// <summary>Find the named storage provider loaded in this silo. </summary>
        internal IStorageProvider GetStorageProvider(string name) => CheckReturnBoundaryReference("storage provider", (IStorageProvider)silo.StorageProviderManager.GetProvider(name));

        private static T CheckReturnBoundaryReference<T>(string what, T obj) where T : class
        {
            if (obj == null) return null;
            if (obj is MarshalByRefObject || obj is ISerializable)
            {
                // Reference to the provider can safely be passed across app-domain boundary in unit test process
                return obj;
            }
            throw new InvalidOperationException(
                $"Cannot return reference to {what} {TypeUtils.GetFullName(obj.GetType())} if it is not MarshalByRefObject or Serializable");
        }

        public IDictionary<GrainId, IGrainInfo> GetDirectoryForTypeNamesContaining(string expr)
        {
            var x = new Dictionary<GrainId, IGrainInfo>();
            foreach (var kvp in ((LocalGrainDirectory)silo.LocalGrainDirectory).DirectoryPartition.GetItems())
            {
                if (kvp.Key.IsSystemTarget || kvp.Key.IsClient || !kvp.Key.IsGrain)
                    continue;// Skip system grains, system targets and clients
                if (((Catalog)silo.Catalog).GetGrainTypeName(kvp.Key).Contains(expr))
                    x.Add(kvp.Key, kvp.Value);
            }
            return x;
        }
        
        // store silos for which we simulate faulty communication
        // number indicates how many percent of requests are lost
        private ConcurrentDictionary<IPEndPoint, double> simulatedMessageLoss;
        private readonly SafeRandom random = new SafeRandom();

        internal void BlockSiloCommunication(IPEndPoint destination, double lossPercentage)
        {
            if (simulatedMessageLoss == null)
                simulatedMessageLoss = new ConcurrentDictionary<IPEndPoint, double>();

            simulatedMessageLoss[destination] = lossPercentage;

            var mc = (MessageCenter)silo.LocalMessageCenter;
            mc.ShouldDrop = ShouldDrop;
        }

        internal void UnblockSiloCommunication()
        {
            var mc = (MessageCenter)silo.LocalMessageCenter;
            mc.ShouldDrop = null;
            simulatedMessageLoss.Clear();
        }
        
        private bool ShouldDrop(Message msg)
        {
            if (simulatedMessageLoss != null)
            {
                double blockedpercentage;
                simulatedMessageLoss.TryGetValue(msg.TargetSilo.Endpoint, out blockedpercentage);
                return (random.NextDouble() * 100 < blockedpercentage);
            }
            else
                return false;
        }
    }
}

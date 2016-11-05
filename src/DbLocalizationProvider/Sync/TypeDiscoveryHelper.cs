﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;
using DbLocalizationProvider.Internal;

namespace DbLocalizationProvider.Sync
{
    internal class TypeDiscoveryHelper
    {
        internal static ConcurrentDictionary<string, List<string>> DiscoveredResourceCache = new ConcurrentDictionary<string, List<string>>();

        internal static List<List<Type>> GetTypes(params Func<Type, bool>[] filters)
        {
            if(filters == null)
            {
                throw new ArgumentNullException(nameof(filters));
            }

            var result = new List<List<Type>>();
            for (var i = 0; i < filters.Length; i++)
            {
                result.Add(new List<Type>());
            }

            var assemblies = GetAssemblies();
            foreach (var assembly in assemblies)
            {
                try
                {
                    var types = assembly.GetTypes();
                    for (var i = 0; i < filters.Length; i++)
                    {
                        result[i].AddRange(types.Where(filters[i]));
                    }
                }
                catch (Exception)
                {
                    // ignored
                }
            }

            return result;
        }

        internal static IEnumerable<Type> GetTypesWithAttribute<T>() where T : Attribute
        {
            return GetTypes(t => t.GetCustomAttribute<T>() != null).FirstOrDefault();
        }

        internal static IEnumerable<Type> GetTypesChildOf<T>()
        {
            var allTypes = new List<Type>();
            foreach (var assembly in GetAssemblies())
            {
                allTypes.AddRange(GetTypesChildOfInAssembly(typeof(T), assembly));
            }

            return allTypes;
        }

        internal static IEnumerable<DiscoveredResource> GetAllProperties(Type type, string keyPrefix = null, bool contextAwareScanning = true)
        {
            var resourceKeyPrefix = type.FullName;
            var typeKeyPrefixSpecified = false;
            var properties = new List<DiscoveredResource>();
            var modelAttribute = type.GetCustomAttribute<LocalizedModelAttribute>();

            if(contextAwareScanning)
            {
                // this is resource class scanning - try to fetch resource key prefix attribute if set there
                var resourceAttribute = type.GetCustomAttribute<LocalizedResourceAttribute>();
                if(!string.IsNullOrEmpty(resourceAttribute?.KeyPrefix))
                {
                    resourceKeyPrefix = resourceAttribute.KeyPrefix;
                    typeKeyPrefixSpecified = true;
                }
                else
                {
                    resourceKeyPrefix = string.IsNullOrEmpty(keyPrefix) ? type.FullName : keyPrefix;
                }
            }
            else
            {
                // this is model scanning - try to fetch resource key prefix attribute if set there
                if(!string.IsNullOrEmpty(modelAttribute?.KeyPrefix))
                {
                    resourceKeyPrefix = modelAttribute.KeyPrefix;
                    typeKeyPrefixSpecified = true;
                }

                var resourceAttributesOnModelClass = type.GetCustomAttributes<ResourceKeyAttribute>().ToList();
                if(resourceAttributesOnModelClass.Any())
                {
                    foreach (var resourceKeyAttribute in resourceAttributesOnModelClass)
                    {
                        properties.Add(new DiscoveredResource(null,
                                                              ResourceKeyBuilder.BuildResourceKey(resourceKeyPrefix, resourceKeyAttribute.Key, separator: string.Empty),
                                                              null,
                                                              resourceKeyAttribute.Value,
                                                              type,
                                                              typeof(string),
                                                              true));
                    }
                }
            }

            if(type.BaseType == typeof(Enum))
            {
                properties.AddRange(type.GetMembers(BindingFlags.Public | BindingFlags.Static)
                                        .Select(mi => new DiscoveredResource(mi,
                                                                             ResourceKeyBuilder.BuildResourceKey(resourceKeyPrefix, mi),
                                                                             mi.Name,
                                                                             mi.Name,
                                                                             type,
                                                                             Enum.GetUnderlyingType(type),
                                                                             Enum.GetUnderlyingType(type).IsSimpleType())).ToList());
            }
            else
            {
                var flags = BindingFlags.Public | BindingFlags.GetProperty | BindingFlags.Instance | BindingFlags.Static;
                if(modelAttribute != null && !modelAttribute.Inherited)
                    flags = flags | BindingFlags.DeclaredOnly;

                properties.AddRange(type.GetProperties(flags)
                                        .Where(pi => pi.GetCustomAttribute<IgnoreAttribute>() == null)
                                        .Where(pi => modelAttribute == null || !modelAttribute.OnlyIncluded || pi.GetCustomAttribute<IncludeAttribute>() != null)
                                        .SelectMany(pi => DiscoverResourcesFromProperty(pi, resourceKeyPrefix, typeKeyPrefixSpecified)).ToList());
            }

            var duplicateKeys = properties.GroupBy(r => r.Key).Where(g => g.Count() > 1).ToList();
            if(duplicateKeys.Any())
            {
                throw new DuplicateResourceKeyException($"Duplicate keys: [{string.Join(", ", duplicateKeys.Select(g => g.Key))}]");
            }

            // first we can filter out all simple and/or complex included properties from the type as starting list of discovered resources
            var results = new List<DiscoveredResource>(properties.Where(t => t.IsSimpleType || t.Info == null || t.Info.GetCustomAttribute<IncludeAttribute>() != null));

            foreach (var property in properties)
            {
                var pi = property.Info;
                var deeperModelType = property.ReturnType;

                if(!property.IsSimpleType)
                {
                    // if this is not a simple type - we need to scan deeper only if deeper model has attribute annotation
                    if(contextAwareScanning || deeperModelType.GetCustomAttribute<LocalizedModelAttribute>() != null)
                    {
                        results.AddRange(GetAllProperties(property.DeclaringType, property.Key, contextAwareScanning));
                    }
                }

                if(pi == null)
                    continue;

                var validationAttributes = pi.GetCustomAttributes<ValidationAttribute>();
                foreach (var validationAttribute in validationAttributes)
                {
                    var resourceKey = ResourceKeyBuilder.BuildResourceKey(property.Key, validationAttribute);
                    var propertyName = resourceKey.Split('.').Last();
                    results.Add(new DiscoveredResource(pi,
                                                       resourceKey,
                                                       string.IsNullOrEmpty(validationAttribute.ErrorMessage) ? propertyName : validationAttribute.ErrorMessage,
                                                       propertyName,
                                                       property.DeclaringType,
                                                       property.ReturnType,
                                                       property.ReturnType.IsSimpleType()));
                }
            }

            // add scanned resources to the cache
            DiscoveredResourceCache.TryAdd(type.FullName, results.Where(r => !string.IsNullOrEmpty(r.PropertyName)).Select(r => r.PropertyName).ToList());

            return results;
        }

        internal static bool IsStringProperty(Type returnType)
        {
            return returnType == typeof(string);
        }

        private static IEnumerable<Assembly> GetAssemblies()
        {
            return ConfigurationContext.Current.AssemblyScanningFilter == null
                       ? AppDomain.CurrentDomain.GetAssemblies()
                       : AppDomain.CurrentDomain.GetAssemblies().Where(ConfigurationContext.Current.AssemblyScanningFilter);
        }

        private static IEnumerable<Type> GetTypesChildOfInAssembly(Type type, Assembly assembly)
        {
            return SelectTypes(assembly, t => t.IsSubclassOf(type) && !t.IsAbstract);
        }

        private static IEnumerable<Type> SelectTypes(Assembly assembly, Func<Type, bool> filter)
        {
            try
            {
                return assembly.GetTypes().Where(filter);
            }
            catch (Exception)
            {
                // there could be situations when type could not be loaded 
                // this may happen if we are visiting *all* loaded assemblies in application domain 
                return new List<Type>();
            }
        }

        private static IEnumerable<DiscoveredResource> DiscoverResourcesFromProperty(PropertyInfo pi, string resourceKeyPrefix, bool typeKeyPrefixSpecified)
        {
            // check if there are [ResourceKey] attributes
            var keyAttributes = pi.GetCustomAttributes<ResourceKeyAttribute>().ToList();
            var translation = GetResourceValue(pi);

            if(!keyAttributes.Any())
            {
                yield return new DiscoveredResource(pi,
                                                    ResourceKeyBuilder.BuildResourceKey(resourceKeyPrefix, pi),
                                                    translation,
                                                    pi.Name,
                                                    pi.PropertyType,
                                                    pi.GetMethod.ReturnType,
                                                    pi.GetMethod.ReturnType.IsSimpleType());

                // try to fetch also [Display()] attribute to generate new "...-Description" resource => usually used for help text labels
                var displayAttribute = pi.GetCustomAttribute<DisplayAttribute>();
                if(!string.IsNullOrEmpty(displayAttribute?.Description))
                {
                    yield return new DiscoveredResource(pi,
                                                        $"{ResourceKeyBuilder.BuildResourceKey(resourceKeyPrefix, pi)}-Description",
                                                        $"{pi.Name}-Description",
                                                        displayAttribute.Description,
                                                        pi.PropertyType,
                                                        pi.GetMethod.ReturnType,
                                                        pi.GetMethod.ReturnType.IsSimpleType());
                }
            }

            foreach (var resourceKeyAttribute in keyAttributes)
            {
                yield return new DiscoveredResource(pi,
                                                    ResourceKeyBuilder.BuildResourceKey(typeKeyPrefixSpecified ? resourceKeyPrefix : null,
                                                                                        resourceKeyAttribute.Key,
                                                                                        separator: string.Empty),
                                                    string.IsNullOrEmpty(resourceKeyAttribute.Value) ? translation : resourceKeyAttribute.Value,
                                                    null,
                                                    pi.PropertyType,
                                                    pi.GetMethod.ReturnType,
                                                    true);
            }
        }

        private static string GetResourceValue(PropertyInfo pi)
        {
            var result = pi.Name;

            // try to extract resource value
            var methodInfo = pi.GetGetMethod();
            if(IsStringProperty(methodInfo.ReturnType))
            {
                try
                {
                    if(methodInfo.IsStatic)
                    {
                        result = methodInfo.Invoke(null, null) as string;
                    }
                    else
                    {
                        if(pi.DeclaringType != null)
                        {
                            var targetInstance = Activator.CreateInstance(pi.DeclaringType);
                            var propertyReturnValue = methodInfo.Invoke(targetInstance, null) as string;
                            if(propertyReturnValue != null)
                            {
                                result = propertyReturnValue;
                            }
                        }
                    }
                }
                catch
                {
                    // if we fail to retrieve value for the resource - fair enough
                }
            }

            var attributes = pi.GetCustomAttributes(true);
            var displayAttribute = attributes.OfType<DisplayAttribute>().FirstOrDefault();

            if(!string.IsNullOrEmpty(displayAttribute?.GetName()))
            {
                result = displayAttribute.GetName();
            }

            var displayNameAttribute = attributes.OfType<DisplayNameAttribute>().FirstOrDefault();
            if(!string.IsNullOrEmpty(displayNameAttribute?.DisplayName))
            {
                result = displayNameAttribute.DisplayName;
            }

            return result;
        }
    }
}
using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace ValheimModPack.PartyPrison
{
    // Reflection is confined to adapter construction. Ordinary item tests and
    // real backpack access use typed delegates after the installed API is bound.
    internal sealed class BackpackAccess<TItem, TInventory> where TItem : class where TInventory : class
    {
        internal Func<TItem, bool> IsBackpack;
        internal Func<TItem, TInventory> Inventory;
        internal Func<TItem, object> Component;
        internal Action<object> Serialize;

        internal static BackpackAccess<TItem, TInventory> Create(Type api, Type extensions, Type componentType)
        {
            if (api == null || extensions == null || componentType == null) throw new NotSupportedException("Backpack persistence API is unavailable.");
            const BindingFlags statics = BindingFlags.Public | BindingFlags.Static;
            MethodInfo isBackpack = api.GetMethod("IsBackpack", statics, null, new[] { typeof(TItem) }, null);
            MethodInfo inventory = api.GetMethod("GetBackpackInventory", statics, null, new[] { typeof(TItem) }, null);
            MethodInfo data = extensions.GetMethod("Data", statics, null, new[] { typeof(TItem) }, null);
            if (isBackpack == null || isBackpack.ReturnType != typeof(bool) || inventory == null || inventory.ReturnType != typeof(TInventory) || data == null)
                throw new MissingMethodException("AdventureBackpacks", "IsBackpack/GetBackpackInventory/Data");
            MethodInfo generic = data.ReturnType.GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(m => m.Name == "GetOrCreate" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
            MethodInfo serialize = componentType.GetMethod("Serialize", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (generic == null || serialize == null || serialize.ReturnType != typeof(string) && serialize.ReturnType != typeof(void))
                throw new MissingMethodException("Backpack", "GetOrCreate/Serialize");
            var isBag = (Func<TItem, bool>)Delegate.CreateDelegate(typeof(Func<TItem, bool>), isBackpack);
            var getInventory = (Func<TItem, TInventory>)Delegate.CreateDelegate(typeof(Func<TItem, TInventory>), inventory);
            MethodInfo bind = typeof(BackpackAccess<TItem, TInventory>).GetMethod("Bind", BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(data.ReturnType, componentType);
            return (BackpackAccess<TItem, TInventory>)bind.Invoke(null, new object[] { isBag, getInventory, data, generic.MakeGenericMethod(componentType), serialize });
        }

        private static BackpackAccess<TItem, TInventory> Bind<TInfo, TComponent>(Func<TItem, bool> isBag, Func<TItem, TInventory> getInventory, MethodInfo data, MethodInfo getOrCreate, MethodInfo serialize) where TInfo : class where TComponent : class
        {
            var getData = (Func<TItem, TInfo>)Delegate.CreateDelegate(typeof(Func<TItem, TInfo>), data);
            var getComponent = (Func<TInfo, string, TComponent>)Delegate.CreateDelegate(typeof(Func<TInfo, string, TComponent>), getOrCreate);
            Action<object> save;
            if (serialize.ReturnType == typeof(string))
            {
                var saveString = (Func<TComponent, string>)Delegate.CreateDelegate(typeof(Func<TComponent, string>), serialize);
                save = value => { saveString((TComponent)value); };
            }
            else
            {
                var saveVoid = (Action<TComponent>)Delegate.CreateDelegate(typeof(Action<TComponent>), serialize);
                save = value => saveVoid((TComponent)value);
            }
            return new BackpackAccess<TItem, TInventory> { IsBackpack = isBag, Inventory = getInventory, Serialize = save,
                Component = item => {
                    TInfo holder = getData(item);
                    if (holder == null) throw new InvalidDataException("Backpack item data is unavailable; contents retained.");
                    TComponent component = getComponent(holder, "");
                    if (component == null) throw new InvalidDataException("Backpack component is unavailable; contents retained.");
                    return component;
                } };
        }
    }

    internal sealed class BackpackAccessCache<TItem, TInventory> where TItem : class where TInventory : class
    {
        private BackpackAccess<TItem, TInventory> value;
        private Exception failure;
        private float nextResolution;
        internal BackpackAccess<TItem, TInventory> Value { get { return value; } }
        internal BackpackAccess<TItem, TInventory> Get(float now, Func<BackpackAccess<TItem, TInventory>> resolve)
        {
            if (value != null) return value;
            if (now < nextResolution) { if (failure != null) throw failure; return null; }
            nextResolution = now + 10;
            try { value = resolve(); failure = null; return value; }
            catch (Exception e) { failure = e; throw; }
        }
    }
}

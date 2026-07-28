namespace MyMod
{
    /// <summary>
    /// Standalone plugin entry when building MyMod.dll alone. Not compiled into MultiSheet.
    /// </summary>
    public sealed class FlamiePracPlugin : IPuckPlugin
    {
        private readonly Class1 inner = new Class1();

        public bool OnEnable() => inner.OnEnable();

        public bool OnDisable() => inner.OnDisable();
    }
}

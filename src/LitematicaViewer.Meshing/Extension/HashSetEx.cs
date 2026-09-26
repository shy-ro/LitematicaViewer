namespace LitematicaViewer.Meshing.Extension;

internal static class HashSetEx
{
    extension<T>(HashSet<T> hashset)
    {
        public void AddMany(IEnumerable<T> collection)
        {
            foreach (var v in collection) hashset.Add(v);
        }
    }
}

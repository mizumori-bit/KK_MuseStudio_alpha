using System;
using System.IO;

namespace MuseStudio
{
    // A file written whole (Mood's records, 献精室's): the new text into a part file, the old file aside (a .bak), the part
    // into its place - the commit; then the leftovers cleared. True once the new text is in place, whatever the clearing does
    // (a .bak that cannot be deleted - read-only, held - is left, and the next write takes another name for its own): the
    // answer, the memory and the file say the same (GPT's re-review of #119, 2). False only with the old file as it was.
    public static class WholeFile
    {
        public static bool Write(string path, string text, out string error)
        {
            error = "";
            string part = path + ".part", bak = path + ".bak";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(part, text, new System.Text.UTF8Encoding(false));
            }
            catch (Exception e) { error = e.Message; TryDelete(part); return false; }
            if (!File.Exists(path))
            {
                try { File.Move(part, path); return true; }
                catch (Exception e) { error = e.Message; TryDelete(part); return false; }
            }
            if (File.Exists(bak) && !TryDelete(bak)) bak = path + ".bak-" + DateTime.Now.Ticks;      // (an old one left: another name)
            try { File.Move(path, bak); }
            catch (Exception e) { error = e.Message; TryDelete(part); return false; }               // (the old file still in place)
            try { File.Move(part, path); }
            catch (Exception e)
            {
                error = e.Message;
                try { File.Move(bak, path); }                                                      // (the old one back)
                catch (Exception e2) { error += "; the old one not put back: " + e2.Message; }
                TryDelete(part);
                return false;
            }
            TryDelete(bak);              // (the commit is done: a leftover is only a leftover)
            return true;
        }

        static bool TryDelete(string p)
        {
            try
            {
                if (!File.Exists(p)) return true;
                File.SetAttributes(p, FileAttributes.Normal);
                File.Delete(p);
                return true;
            }
            catch (Exception) { return false; }
        }
    }
}

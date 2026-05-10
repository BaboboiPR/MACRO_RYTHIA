using WindowsInput;
using WindowsInput.Native;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics; // Required for Stopwatch
using System.Threading;
using System.Runtime.InteropServices;
using System.Linq;
using System.Text;
// var Sim = new InputSimulator();
//
// Sim.Keyboard.KeyPress(VirtualKeyCode.VK_A);
// Sim.Mouse.MoveMouseTo(32768, 32768);
// // ReSharper disable once InconsistentNaming
// // 2. Click the left mouse button
// Sim.Mouse.LeftButtonClick();
//
// // 3. Move relative to current position
// // (Moves 100 units right, 100 units down)
// Sim.Mouse.MoveMouseBy(100, 100);


TAS Tas = new TAS();
Tas.Main();
public class SSPMConverter
{
    public struct Note
    {
        public float X;
        public float Y;
        public int Ms;
    }

    public static void ConvertSspmToTxt(string sspmPath, string txtPath, double speedMultiplier = 1.0)
    {
        using (FileStream fs = new FileStream(sspmPath, FileMode.Open, FileAccess.Read))
        using (BinaryReader br = new BinaryReader(fs))
        {
            // 1. Signature Check
            string signature = Encoding.ASCII.GetString(br.ReadBytes(4));
            if (signature != "SS+m") throw new Exception("Invalid SSPM Signature");

            // 2. Version
            ushort version = br.ReadUInt16();
            List<Note> notes = new List<Note>();

            if (version == 1)
            {
                br.ReadBytes(2); // Skip 2 bytes
                ReadNullTerminatedString(br); // mapId
                ReadNullTerminatedString(br); // mapName
                ReadNullTerminatedString(br); // mappers

                br.ReadUInt32(); // lastMs
                uint noteCount = br.ReadUInt32();
                br.ReadByte();   // difficulty

                byte containsCover = br.ReadByte();
                if (containsCover == 0x02) br.ReadBytes((int)br.ReadUInt64());

                bool containsAudio = br.ReadBoolean();
                if (containsAudio) br.ReadBytes((int)br.ReadUInt64());

                for (int i = 0; i < noteCount; i++)
                {
                    uint ms = br.ReadUInt32();
                    bool isQuantum = br.ReadBoolean();
                    float x, y;
                    if (isQuantum) { x = br.ReadSingle(); y = br.ReadSingle(); }
                    else { x = br.ReadByte(); y = br.ReadByte(); }

                    notes.Add(new Note { X = x, Y = y, Ms = (int)(ms / speedMultiplier) });
                }
            }
            else if (version == 2)
            {
                br.ReadBytes(24); // Skip header padding
                br.ReadUInt32(); // lastMs
                uint noteCount = br.ReadUInt32();
                br.ReadUInt32(); // markerCount
                
                br.ReadByte();   // difficulty
                br.ReadUInt16(); // rating
                br.ReadBoolean(); // audio
                br.ReadBoolean(); // cover
                br.ReadBoolean(); // mod

                br.ReadUInt64(); br.ReadUInt64(); // customData
                br.ReadUInt64(); br.ReadUInt64(); // audio
                br.ReadUInt64(); br.ReadUInt64(); // cover
                
                long markerDefOffset = (long)br.ReadUInt64();
                br.ReadUInt64(); // markerDefLength
                long markerOffset = (long)br.ReadUInt64();
                br.ReadUInt64(); // markerLength

                // Version 2 strings are usually Prefixed Strings
                ReadVarString(br); // id
                ReadVarString(br); // name
                ReadVarString(br); // song

                // Jump to markers
                fs.Seek(markerOffset, SeekOrigin.Begin);
                for (int i = 0; i < noteCount; i++)
                {
                    uint ms = br.ReadUInt32();
                    br.ReadByte(); // markerType
                    bool isQuantum = br.ReadBoolean();
                    float x, y;
                    if (isQuantum) { x = br.ReadSingle(); y = br.ReadSingle(); }
                    else { x = br.ReadByte(); y = br.ReadByte(); }

                    notes.Add(new Note { X = x, Y = y, Ms = (int)(ms / speedMultiplier) });
                }
            }

            // Sort by time (just like the Python code)
            var sortedNotes = notes.OrderBy(n => n.Ms).ToList();

            // Save to TXT
            using (StreamWriter sw = new StreamWriter(txtPath))
            {
                foreach (var note in sortedNotes)
                {
                    sw.WriteLine($"{note.X}|{note.Y}|{note.Ms}");
                }
                sw.WriteLine($"\n\n# Total Notes: {sortedNotes.Count}");
            }
        }
    }

    private static string ReadNullTerminatedString(BinaryReader br)
    {
        List<byte> bytes = new List<byte>();
        byte b;
        while ((b = br.ReadByte()) != 10 && b != 0) // Stop at \n or null
        {
            bytes.Add(b);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static string ReadVarString(BinaryReader br)
    {
        ushort len = br.ReadUInt16();
        return Encoding.UTF8.GetString(br.ReadBytes(len));
    }
}

class TAS
{
    private readonly InputSimulator _Sim = new InputSimulator();
    const int ScreenWidth = 1920;
    const int ScreenHeight = 1080;
    
    public List<(int X, int Y, double Time)> ParseFile(string path)
    {
        var Actions = new List<(int, int, double)>();


        const int GridSize = 625;
        int CellSize = GridSize / 3;
        int GridStartX = ScreenWidth / 2 - GridSize / 2;
        int GridStartY = ScreenHeight / 2 - GridSize / 2;
        try
        {
            foreach (string line in File.ReadLines(path))
            {
                string[] Parts = line.Trim().Split('|');
                if (!double.TryParse(Parts[0], out double Col)) continue;
                if (!double.TryParse(Parts[1], out double Row)) continue;
                if (!double.TryParse(Parts[2], out double NoteTimeMs)) continue;
                double X = GridStartX + (Col * CellSize) + (CellSize / 2.0);
                double Y = GridStartY + (Row * CellSize) + (CellSize / 2.0);
                Actions.Add(((int)Math.Round(X), (int)Math.Round(Y), NoteTimeMs));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to open file: {ex.Message}");
        }

        return Actions;
    }

    void SendAbsoluteMouseMove(int X, int Y)
    {
        double SimX = (double)X / ScreenWidth * 65535;
        double SimY = (double)Y / ScreenHeight * 65535;
        
        _Sim.Mouse.MoveMouseTo(SimX, SimY);
    }

    void RunMacro(List<(int X, int Y,double Time)> Actions, TimeSpan SleepBeforeStart)
    {
        Thread.Sleep(SleepBeforeStart);

        // Stopwatch is the C# equivalent of Rust's Instant
        Stopwatch sw = Stopwatch.StartNew();
        
        foreach (var (X,Y,Time) in Actions)
        {
            var Target = TimeSpan.FromMilliseconds(Time);
            TimeSpan Elapsed = sw.Elapsed;
            if (Target > Elapsed)
            {
                Thread.Sleep(Target - Elapsed);
            }
            SendAbsoluteMouseMove(X, Y);
        }
    }

    TimeSpan ChooseSleepDuration()
    {
        var DefaultSleep = new TimeSpan(0, 0, 0, 1, 550);
        Console.WriteLine("Choose sleep preset before start:");
        Console.WriteLine("1) Default " + DefaultSleep.ToString());
        Console.WriteLine("2) Custom (enter seconds)");
        string Choice = "";
        Choice = Console.ReadLine();
        if (Choice == "1")
        {
            return DefaultSleep;
        }
        else
        {
            Console.WriteLine("Enter custom sleep duration in seconds (e.g 4): ");
            int Seconds = 0;
            int Ms = 0;
            try
            {
                Seconds = Convert.ToInt32(Console.ReadLine());
                Console.WriteLine("Enter a sleep duration in ms");
                Ms = Convert.ToInt32(Console.ReadLine());
            }
            catch (FormatException)
            {
                Console.WriteLine("Invalid sleep duration");
            }

            var CustomSleep = new TimeSpan(0, 0, 0, 0, Ms + 1000 * Seconds);
            return CustomSleep;
        }
    }

    public void Main()
    {
        while (true)
        {
            string text = "hard_map.sspm";
            //text = Console.ReadLine();
            SSPMConverter.ConvertSspmToTxt(text,text + ".txt",1.0);

            var Sleep = ChooseSleepDuration();
            Console.WriteLine("Press SPACE to continue...");
            
            KeyboardHook Keyboard = new KeyboardHook();
            Keyboard.WaitForKey(0x20);
            
            var Actions = ParseFile(text+".txt");
            RunMacro(Actions, Sleep);
            Console.WriteLine("Finished macro for file: " + text +".txt");
        }
    }
    
}

class KeyboardHook
{
    // Imports the Win32 function to check if a key is currently pressed
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    // Virtual Key Codes (F5 = 0x74, Space = 0x20, etc.)
    public void WaitForKey(int virtualKey)
    {
        while (true)
        {
            // 0x8000 checks the "High-order bit" to see if the key is down
            if ((GetAsyncKeyState(virtualKey) & 0x8000) != 0)
            {
                break; // Key was pressed!
            }
            Thread.Sleep(10); // Don't max out the CPU
        }
    }
}

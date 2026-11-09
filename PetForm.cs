using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace DesktopPet;

/*
 * Desktop pet: піксельна істота, яка ходить, стрибає і має 140 емоцій.
 *
 * Керування:
 *   - клік лівою кнопкою      -> злиться і підстрибує
 *   - клікати дуже швидко     -> лютує
 *   - ліва кнопка + тягнути   -> можна перенести (дивується, після відпускання злиться)
 *   - колесо миші над нею     -> лоскоче (сміється)
 *   - права кнопка миші       -> меню: Погодувати / Емоції / Події / Спати / Вийти
 *
 * Настрій змінюється через події: курсор, простій користувача, час доби, голод,
 * падіння, удар об край екрана, батарея, активна програма.
 */
public sealed class PetForm : Form
{
    // ---------- налаштування ----------
    private const int Px = 9;                      // розмір одного "пікселя" на екрані
    private const int OutlineW = 3;                // товщина білої обводки
    private const bool PrintNames = true;          // писати емоцію та причину в консоль
    private const bool RandomMoods = false;        // true -> ще й зрідка міняє настрій без причини
    private const double HungerAfter = 15 * 60;    // секунд до голоду після годування
    private const double IdleSadAfter = 90;        // секунд без дій користувача -> нудьгує
    private const double IdleSleepAfter = 240;     // секунд без дій користувача -> засинає

    // ---------- сітка ----------
    private const int GW = 13, GH = 13, PadX = 1, PadTop = 5;
    private static readonly Color Trans = Color.FromArgb(1, 1, 1);

    private static Color Hex(int rgb) => Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);

    private static readonly Dictionary<char, Color> Col = new()
    {
        ['K'] = Hex(0x141414), ['W'] = Hex(0xffffff), ['P'] = Hex(0xff6fa5), ['Y'] = Hex(0xffd23f),
        ['R'] = Hex(0xff3b3b), ['C'] = Hex(0x5bc0ff), ['G'] = Hex(0x8a8a8a), ['Z'] = Hex(0x7a6cff),
    };

    private static readonly Dictionary<string, Color> Tints = new()
    {
        ["normal"] = Hex(0xdd7a5a), ["red"] = Hex(0xe5533a), ["green"] = Hex(0xa9c46c),
        ["blue"] = Hex(0x86b4d6), ["pink"] = Hex(0xf08ba5), ["gray"] = Hex(0xa8a39e),
        ["pale"] = Hex(0xecd2c2), ["gold"] = Hex(0xe8b84a), ["purple"] = Hex(0xb78fd0),
    };

    private static readonly string[] Body =
    {
        ".BBBBBBBBB.",
        ".BBBBBBBBB.",
        "BBBBBBBBBBB",
        "BBBBBBBBBBB",
        "BBBBBBBBBBB",
        ".BBBBBBBBB.",
    };

    private static readonly Dictionary<string, string[]> Legs = new()
    {
        ["idle"] = new[] { "..BB...BB..", "..BB...BB.." },
        ["a"]    = new[] { "..BB...BB..", "..BB......." },
        ["b"]    = new[] { "..BB...BB..", ".......BB.." },
        ["jump"] = new[] { "..BB...BB..", "..........." },
    };

    private const string DefaultEmo = "Радість", AngryEmo = "Злість", FuryEmo = "Лють";
    private const string SurprisedEmo = "Подив", WakeEmo = "Здивування", SleepEmo = "Сон";

    // у цих емоціях істота не може рухатись (лише падає, якщо в повітрі)
    private static readonly HashSet<string> Frozen = new() { "Шок", "Знепритомнів" };

    private enum State { Idle, Walk, Sleep, Air, Drag }

    // ---------- стан ----------
    private readonly Random rng = new();
    private readonly System.Windows.Forms.Timer timer;
    private readonly ContextMenuStrip menu = new();

    private readonly int w, h;
    private readonly int minX, maxX;
    private readonly double ground;

    private double x, y, vx, vy;
    private int facing = 1;
    private State state = State.Air;
    private int stateT;
    private string emotion = DefaultEmo;
    private int emotionT = -1;          // -1 = постійна емоція
    private int prio;                   // важливість поточної емоції
    private string legs = "a";
    private int frame;
    private (string, bool, string, int)? lastKey;

    // миша
    private bool moved;
    private int pressX, pressY;
    private double offX, offY;
    private readonly List<double> clickTimes = new();

    // події
    private readonly Dictionary<string, double> cool = new();
    private Point lastPtr = Cursor.Position;
    private double lastD = 9999;
    private double? hoverSince;
    private bool hoverDone;
    private bool afk;
    private int lastHour = -1;
    private double lastFed = Now();
    private string? lastApp;
    private bool? lastPlugged;

    // малювання
    private char[,]? grid;
    private bool[,]? mask;
    private Color bodyColor = Tints["normal"];
    private static readonly Dictionary<char, SolidBrush> ColBrush =
        Col.ToDictionary(kv => kv.Key, kv => new SolidBrush(kv.Value));

    private static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public PetForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Trans;
        TransparencyKey = Trans;
        DoubleBuffered = true;

        w = GW * Px + 2 * OutlineW;
        h = GH * Px + 2 * OutlineW;
        ClientSize = new Size(w, h);

        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1040);
        minX = wa.Left;
        maxX = wa.Right - w;
        ground = wa.Bottom - h;     // стоїть просто на панелі завдань
        x = rng.Next(wa.Left + 100, Math.Max(wa.Left + 101, wa.Right - 200));
        y = wa.Top;

        BuildMenu();

        timer = new System.Windows.Forms.Timer { Interval = 30 };
        timer.Tick += (_, _) => Tick();
        timer.Start();
    }

    // не крадемо фокус і не показуємось в Alt+Tab
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80;     // WS_EX_TOOLWINDOW
            return cp;
        }
    }

    // ---------- меню ----------
    private void BuildMenu()
    {
        menu.Items.Add(new ToolStripMenuItem("Погодувати", null, (_, _) => Feed()));
        menu.Items.Add(new ToolStripSeparator());

        var emoMenu = new ToolStripMenuItem($"Емоції ({Data.Emotions.Count})");
        foreach (var (title, items) in Data.Groups)
        {
            var sub = new ToolStripMenuItem($"{title} ({items.Length})");
            foreach (var e in items)
            {
                string name = e.Name;
                sub.DropDownItems.Add(new ToolStripMenuItem(name, null, (_, _) => ShowEmotion(name)));
            }
            emoMenu.DropDownItems.Add(sub);
        }
        menu.Items.Add(emoMenu);

        var evMenu = new ToolStripMenuItem("Події (перевірити)");
        foreach (var ev in Data.Events.Keys)
        {
            string key = ev;
            evMenu.DropDownItems.Add(new ToolStripMenuItem(
                Data.EventLabels.GetValueOrDefault(key, key), null, (_, _) => React(key, force: true)));
        }
        menu.Items.Add(evMenu);

        menu.Items.Add(new ToolStripMenuItem("Випадкова емоція", null, (_, _) => RandomEmotion()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Спати", null, (_, _) => GoSleep()));
        menu.Items.Add(new ToolStripMenuItem("Стрибнути", null, (_, _) => Jump()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Вийти", null, (_, _) => Close()));
    }

    // ---------- емоції ----------
    private void SetEmotion(string name, int frames = -1, int pr = 0)
    {
        emotion = name;
        emotionT = frames;
        prio = pr;
    }

    private void ShowEmotion(string name, int frames = 300, int pr = 0, string why = "")
    {
        if (PrintNames)
            Console.WriteLine(why.Length > 0 ? $"[{why}] Емоція: {name}" : $"Емоція: {name}");
        SetEmotion(name, frames, pr);
    }

    private void RandomEmotion() =>
        ShowEmotion(Data.RandomPool[rng.Next(Data.RandomPool.Length)], rng.Next(120, 301), 0, "випадково");

    // ---------- події ----------
    /// <summary>Подія -> емоція. Враховує паузу між повторами та важливість поточної емоції.</summary>
    private void React(string ev, int? frames = null, bool force = false)
    {
        if (state == State.Drag || !Data.Events.TryGetValue(ev, out var names)) return;

        double now = Now();
        int p = Data.EventPrio.GetValueOrDefault(ev, 1);
        if (!force)
        {
            double last = cool.GetValueOrDefault(ev, -1e9);
            if (now - last < Data.EventCooldown.GetValueOrDefault(ev, 20)) return;
            if (state == State.Sleep && !Data.WakeEvents.Contains(ev)) return;
            if (emotionT != -1 && p < prio) return;
        }

        cool[ev] = now;
        if (state == State.Sleep)               // подія будить
        {
            state = State.Idle;
            stateT = 30;
            vx = 0;
        }
        string name = names[rng.Next(names.Length)];
        ShowEmotion(name, frames ?? rng.Next(150, 301), p, Data.EventLabels.GetValueOrDefault(ev, ev));
    }

    private void Feed()
    {
        lastFed = Now();
        React("fed", force: true);
    }

    private void CheckMouse()
    {
        var p = Cursor.Position;
        double cx = x + w / 2.0, cy = y + h / 2.0;
        double d = Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy));
        double step = Math.Sqrt((p.X - lastPtr.X) * (p.X - lastPtr.X) + (p.Y - lastPtr.Y) * (p.Y - lastPtr.Y));
        bool approaching = d < lastD - 20;

        if (d < 260 && step > 70 && approaching)
            React("mouse_fast");
        else if (d < 170 && lastD >= 170)
            React("mouse_near");

        if (d < w / 2.0)
        {
            double now = Now();
            if (hoverSince is null) hoverSince = now;
            else if (now - hoverSince > 2.5 && !hoverDone)
            {
                hoverDone = true;
                React("mouse_hover");
            }
        }
        else
        {
            hoverSince = null;
            hoverDone = false;
        }

        lastPtr = p;
        lastD = d;
    }

    private void CheckSlow()
    {
        var now = DateTime.Now;

        // час доби (при зміні години та при запуску)
        if (now.Hour != lastHour)
        {
            lastHour = now.Hour;
            int hr = now.Hour;
            string? ev = null;
            if (hr >= 23 || hr < 5) ev = "night";
            else if (hr < 11) ev = "morning";
            else if (hr >= 12 && hr < 14) ev = "lunch";
            else if (hr >= 18) ev = "evening";
            if (ev != null) React(ev);
        }

        // користувач пішов / повернувся
        double idle = Native.UserIdleSeconds();
        if (!afk && idle > IdleSadAfter)
        {
            afk = true;
            React("user_idle");
        }
        else if (afk && idle > IdleSleepAfter && (state == State.Idle || state == State.Walk))
        {
            GoSleep(longSleep: true);
        }
        else if (afk && idle < 3)
        {
            afk = false;
            React("user_back");
        }

        // голод
        if (Now() - lastFed > HungerAfter)
            React("hungry");
    }

    private void CheckApp()
    {
        string title = Native.ForegroundTitle();
        string? cat = null;
        foreach (var (words, ev) in Data.AppRules)
        {
            if (words.Any(wd => title.Contains(wd))) { cat = ev; break; }
        }
        if (cat != null && cat != lastApp) React(cat);
        lastApp = cat;
    }

    private void CheckBattery()
    {
        var info = Native.Battery();
        if (info is null) return;
        var (pct, plugged) = info.Value;
        if (!plugged && pct <= 20) React("battery_low");
        if (lastPlugged == false && plugged) React("battery_ok");
        lastPlugged = plugged;
    }

    private void CheckEvents()
    {
        if (state == State.Drag) return;
        if (frame % 5 == 0) CheckMouse();
        if (frame % 33 == 0) CheckSlow();
        if (frame % 66 == 0) CheckApp();
        if (frame == 10 || frame % 1000 == 0) CheckBattery();
    }

    // ---------- дії ----------
    private void Jump()
    {
        if (state is State.Air or State.Drag || Frozen.Contains(emotion)) return;
        vy = -14;
        vx = facing * rng.Next(2, 6);
        state = State.Air;
    }

    private void GoSleep() => GoSleep(false);

    private void GoSleep(bool longSleep)
    {
        if (state is State.Air or State.Drag) return;
        state = State.Sleep;
        stateT = longSleep ? 1_000_000_000 : rng.Next(200, 501);
        vx = 0;
        SetEmotion(SleepEmo);
    }

    private void ChooseNext()
    {
        double r = rng.NextDouble();
        if (r < 0.5)
        {
            state = State.Walk;
            stateT = rng.Next(40, 151);
            facing = rng.Next(2) == 0 ? -1 : 1;
        }
        else if (r < 0.75) Jump();
        else if (r < 0.85) GoSleep();
        else
        {
            state = State.Idle;
            stateT = rng.Next(30, 101);
        }
    }

    // ---------- миша ----------
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        var p = Cursor.Position;
        moved = false;
        pressX = p.X;
        pressY = p.Y;
        offX = p.X - x;
        offY = p.Y - y;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if ((e.Button & MouseButtons.Left) == 0) return;
        var p = Cursor.Position;
        if (!moved && Math.Abs(p.X - pressX) + Math.Abs(p.Y - pressY) < 4) return;   // дрібне тремтіння = клік
        moved = true;
        state = State.Drag;
        x = p.X - offX;
        y = p.Y - offY;
        vx = vy = 0;
        SetEmotion(SurprisedEmo, -1, 6);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Right)
        {
            menu.Show(Cursor.Position);
            return;
        }
        if (e.Button != MouseButtons.Left) return;

        if (moved)
        {
            state = State.Air;
            SetEmotion(FuryEmo, 70, 4);
            return;
        }

        double now = Now();
        clickTimes.RemoveAll(t => now - t >= 2.5);
        clickTimes.Add(now);
        if (clickTimes.Count >= 4)                  // дуже багато кліків
        {
            clickTimes.Clear();
            React("click_spam", force: true);
        }
        else                                        // звичайний клік -> злість + підскок
        {
            SetEmotion(AngryEmo, 90, 4);
        }
        if (state is State.Sleep or State.Idle or State.Walk)
        {
            vy = -9;
            vx = 0;
            state = State.Air;
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        React("pet");
    }

    // ---------- головний цикл ----------
    private void Tick()
    {
        frame++;

        // таймер емоції
        if (emotionT > 0)
        {
            emotionT--;
            if (emotionT == 0)
            {
                emotion = state == State.Sleep ? SleepEmo : DefaultEmo;
                emotionT = -1;
                prio = 0;
            }
        }

        CheckEvents();

        if (RandomMoods && emotion == DefaultEmo && emotionT == -1
            && (state == State.Idle || state == State.Walk) && rng.NextDouble() < 0.0003)
            RandomEmotion();

        bool blink = frame % 140 < 4;

        if (state != State.Drag)
        {
            bool frozen = Frozen.Contains(emotion);
            if (frozen)
            {
                vx = 0;
                if (state == State.Walk) { state = State.Idle; stateT = 30; }
            }

            // фізика
            vy += 0.9;
            y += vy;
            x += vx;
            if (y >= ground)
            {
                y = ground;
                double impact = vy;
                if (state == State.Air)
                {
                    state = State.Idle;
                    stateT = rng.Next(20, 81);
                    vx = 0;
                    if (emotion == SurprisedEmo) SetEmotion(DefaultEmo);
                    if (impact > 26) React("fall_hard", force: true);   // впала з великої висоти
                }
                vy = 0;
            }
            if (x < minX)
            {
                x = minX; vx = Math.Abs(vx); facing = 1;
                if (state == State.Walk && rng.NextDouble() < 0.6) React("wall");
            }
            else if (x > maxX)
            {
                x = maxX; vx = -Math.Abs(vx); facing = -1;
                if (state == State.Walk && rng.NextDouble() < 0.6) React("wall");
            }

            // поведінка на землі
            switch (state)
            {
                case State.Idle:
                    vx = 0;
                    if (--stateT <= 0 && !frozen) ChooseNext();
                    break;
                case State.Walk:
                    vx = facing * 3;
                    stateT--;
                    if (frame % 6 == 0) legs = legs == "a" ? "b" : "a";
                    if (stateT <= 0)
                    {
                        vx = 0;
                        state = State.Idle;
                        stateT = rng.Next(30, 101);
                    }
                    break;
                case State.Sleep:
                    vx = 0;
                    if (--stateT <= 0)
                    {
                        state = State.Idle;
                        stateT = 30;
                        SetEmotion(WakeEmo, 25);
                    }
                    break;
            }
        }

        string legsName = state is State.Air or State.Drag ? "jump" : (state == State.Walk ? legs : "idle");

        var emo = Data.Emotions[emotion];
        blink = blink && Data.Blinkable.Contains(emo.Eyes);
        var key = (emotion, blink, legsName, facing);
        if (lastKey is null || lastKey.Value != key)
        {
            Rebuild(emotion, blink, legsName, facing);
            lastKey = key;
        }

        // рух залежно від емоції
        int dx = emo.Motion == "shake" ? (rng.Next(2) == 0 ? -3 : 3) : 0;
        int dy = emo.Motion == "bob" && state is not (State.Air or State.Drag) && (frame / 5) % 2 == 1 ? -4 : 0;
        Location = new Point((int)x + dx, (int)y + dy);
    }

    // ---------- малювання ----------
    private static void Place(char[,] g, int r, int c, char ch)
    {
        int rr = r + PadTop, cc = c + PadX;
        if (rr >= 0 && rr < GH && cc >= 0 && cc < GW) g[rr, cc] = ch;
    }

    private void Rebuild(string emotionName, bool blink, string legsName, int face)
    {
        var emo = Data.Emotions[emotionName];
        string eyes = blink && Data.Blinkable.Contains(emo.Eyes) ? "flat" : emo.Eyes;

        var g = new char[GH, GW];
        var m = new bool[GH, GW];
        for (int r = 0; r < GH; r++)
            for (int c = 0; c < GW; c++) g[r, c] = '.';

        var rows = Body.Concat(Legs[legsName]).ToArray();
        for (int r = 0; r < rows.Length; r++)
            for (int c = 0; c < rows[r].Length; c++)
                if (rows[r][c] != '.')
                {
                    Place(g, r, c, rows[r][c]);
                    m[r + PadTop, c + PadX] = true;
                }

        foreach (var part in new[] { Data.Effects[emo.Effect], Data.Eyes[eyes], Data.Mouths[emo.Mouth] })
            foreach (var cell in part)
                Place(g, cell.R, cell.C, cell.Ch);

        if (face < 0)   // дзеркально
        {
            var g2 = new char[GH, GW];
            var m2 = new bool[GH, GW];
            for (int r = 0; r < GH; r++)
                for (int c = 0; c < GW; c++)
                {
                    g2[r, c] = g[r, GW - 1 - c];
                    m2[r, c] = m[r, GW - 1 - c];
                }
            g = g2;
            m = m2;
        }

        grid = g;
        mask = m;
        bodyColor = Tints[emo.Tint];
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var gr = e.Graphics;
        gr.Clear(Trans);
        if (grid is null || mask is null) return;

        const int o = OutlineW, m = OutlineW;

        // 1) біла обводка навколо тіла
        for (int r = 0; r < GH; r++)
            for (int c = 0; c < GW; c++)
                if (mask[r, c])
                    gr.FillRectangle(ColBrush['W'], m + c * Px - o, m + r * Px - o, Px + 2 * o, Px + 2 * o);

        // 2) самі клітинки
        using var body = new SolidBrush(bodyColor);
        for (int r = 0; r < GH; r++)
            for (int c = 0; c < GW; c++)
            {
                char ch = grid[r, c];
                if (ch == '.') continue;
                var brush = ch is 'B' or 'D' ? body : ColBrush[ch];
                gr.FillRectangle(brush, m + c * Px, m + r * Px, Px, Px);
            }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}
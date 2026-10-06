namespace ULTRAKILL.MacPort
{
    // Absolute deadlines avoid accumulated millisecond sleep rounding and catch-up bursts.
    public sealed class FrameDeadline
    {
        double deadline;
        int rate;
        public double Advance(double now, int newRate)
        {
            if (newRate <= 0) { Reset(); return now; }
            double interval = 1.0 / newRate;
            if (rate != newRate || deadline == 0 || now > deadline + interval)
                deadline = now;
            rate = newRate;
            double target = deadline;
            deadline += interval;
            return target;
        }
        public void Reset() { deadline = 0; rate = 0; }
    }
}

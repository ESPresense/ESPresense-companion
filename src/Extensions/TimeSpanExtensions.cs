namespace ESPresense.Extensions
{
    public static class TimeSpanExtensions
    {
        public static bool TryParseDurationString(this string input, out TimeSpan ts)
        {
            ts = default(TimeSpan);
            if (string.IsNullOrWhiteSpace(input))
                return false;

            var total = TimeSpan.Zero;
            var currentNumber = 0;

            foreach (var c in input)
            {
                if (char.IsDigit(c))
                {
                    currentNumber = currentNumber * 10 + (c - '0');
                }
                else
                {
                    switch (char.ToLower(c))
                    {
                        case 'd':
                            total = total.Add(TimeSpan.FromDays(currentNumber));
                            break;
                        case 'h':
                            total = total.Add(TimeSpan.FromHours(currentNumber));
                            break;
                        case 'm':
                            total = total.Add(TimeSpan.FromMinutes(currentNumber));
                            break;
                        case 's':
                            total = total.Add(TimeSpan.FromSeconds(currentNumber));
                            break;
                        default:
                            return false;
                    }
                    currentNumber = 0;
                }
            }

            ts = total;
            return true;
        }
    }
}

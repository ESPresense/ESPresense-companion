namespace ESPresense.Extensions
{
    public enum DurationUnit
    {
        Seconds,
        Minutes, 
        Hours,
        Days
    }

    public static class TimeSpanExtensions
    {
        public static bool TryParseDurationString(this string input, out TimeSpan ts, DurationUnit defaultUnit = DurationUnit.Seconds)
        {
            ts = default(TimeSpan);
            if (string.IsNullOrWhiteSpace(input))
                return false;

            var total = TimeSpan.Zero;
            var currentNumber = 0;
            var hasExplicitUnits = false;
            var hasReadDigits = false; // Track if we've read any digits for current number

            foreach (var c in input)
            {
                if (char.IsDigit(c))
                {
                    currentNumber = currentNumber * 10 + (c - '0');
                    hasReadDigits = true;
                }
                else
                {
                    if (!hasReadDigits)
                        return false; // Found unit without any preceding digits (e.g., "s30")
                    
                    hasExplicitUnits = true;
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
                    hasReadDigits = false; // Reset for next number
                }
            }

            // Handle any remaining number without unit
            if (hasReadDigits)
            {
                // If we had explicit units but also have a unitless number, that's ambiguous - reject it
                if (hasExplicitUnits)
                    return false;

                // Use default unit for purely unitless input
                total = defaultUnit switch
                {
                    DurationUnit.Days => total.Add(TimeSpan.FromDays(currentNumber)),
                    DurationUnit.Hours => total.Add(TimeSpan.FromHours(currentNumber)),
                    DurationUnit.Minutes => total.Add(TimeSpan.FromMinutes(currentNumber)),
                    DurationUnit.Seconds => total.Add(TimeSpan.FromSeconds(currentNumber)),
                    _ => total.Add(TimeSpan.FromSeconds(currentNumber))
                };
            }

            ts = total;
            return true;
        }

        // Backward compatibility overload
        public static bool TryParseDurationString(this string input, out TimeSpan ts) =>
            TryParseDurationString(input, out ts, DurationUnit.Seconds);
    }
}

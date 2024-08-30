using System;

namespace ESPresense.Extensions
{
    public static class DateTimeExtensions
    {
        public static TimeSpan ParseTimeSpan(this string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return TimeSpan.FromHours(24);

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
                            throw new FormatException($"Invalid time span format: {input}");
                    }
                    currentNumber = 0;
                }
            }

            return total == TimeSpan.Zero ? TimeSpan.FromHours(24) : total;
        }
    }
}

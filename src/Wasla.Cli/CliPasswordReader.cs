using System.Text;

namespace Wasla.Cli;

internal readonly record struct CliSecretPrompt(bool Ok, string Value);

internal interface ICliPasswordReader
{
    CliSecretPrompt ReadSecret(string prompt);
}

internal sealed class ConsoleCliPasswordReader : ICliPasswordReader
{
    public CliSecretPrompt ReadSecret(string prompt)
    {
        if (Console.IsInputRedirected)
            return new CliSecretPrompt(false, string.Empty);

        Console.Write(prompt);
        var buffer = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return new CliSecretPrompt(true, buffer.ToString());
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                    buffer.Length--;
                continue;
            }

            if (char.IsControl(key.KeyChar))
                continue;

            buffer.Append(key.KeyChar);
        }
    }
}

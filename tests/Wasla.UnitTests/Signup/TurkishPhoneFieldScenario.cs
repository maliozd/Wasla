using System.Text.Json;
using Wasla.UnitTests.Admin;

namespace Wasla.UnitTests.Signup;

/// <summary>
/// The editing behaviour of a Türkiye phone field bound to the shared <c>js/signup/signup-phone-format.js</c>, run in a
/// real headless Chromium against one field. The signup page and the pilot application form run the same steps, so both
/// are held to one behaviour: a single Backspace right after a group space removes the digit before it, and a single
/// Delete right before one removes the digit after it, with the caret where it belongs; text with several digits
/// inserted in one go (a keyboard suggestion, an input method) ends as a paste of that text does; a single 11th digit
/// typed is still refused. Keyboard input is real (CDP key events and <c>Input.insertText</c>); a paste is a clipboard
/// event, as in the other phone tests.
/// </summary>
internal static class TurkishPhoneFieldScenario
{
    private const string Full = "532 418 27 90";

    public static async Task RunAsync(HeadlessChromium browser, string fieldId, CancellationToken ct)
    {
        var field = $"document.getElementById('{fieldId}')";

        async Task<(string Value, int Caret)> StateAsync()
        {
            using var state = JsonDocument.Parse(await browser.EvaluateAsync<string>(
                $"(() => {{ const p = {field}; return JSON.stringify([p.value, p.selectionStart, p.selectionEnd]); }})()", ct));
            var root = state.RootElement;
            Assert.Equal(root[1].GetInt32(), root[2].GetInt32());
            return (root[0].GetString()!, root[1].GetInt32());
        }

        // The field focused with a formatted value and a collapsed caret, as a visitor leaves it.
        async Task PlaceAsync(string value, int caret) =>
            await browser.EvaluateAsync<bool>($$"""
                (() => { const p = {{field}}; p.focus(); p.value = {{JsonSerializer.Serialize(value)}}; p.setSelectionRange({{caret}}, {{caret}}); return true; })()
                """, ct);

        async Task SelectAllAsync() =>
            await browser.EvaluateAsync<bool>($"(() => {{ const p = {field}; p.focus(); p.select(); return true; }})()", ct);

        async Task PasteOverAllAsync(string text) =>
            await browser.EvaluateAsync<bool>($$"""
                (() => { const p = {{field}}; p.focus(); p.select();
                  const data = new DataTransfer(); data.setData('text', {{JsonSerializer.Serialize(text)}});
                  p.dispatchEvent(new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true })); return true; })()
                """, ct);

        // 1. and 2. One Backspace with the caret right after each group space (4, 8, 11) removes the digit before it.
        foreach (var (caret, value, after) in new[] { (4, "534 182 79 0", 2), (8, "532 412 79 0", 6), (11, "532 418 29 0", 9) })
        {
            await PlaceAsync(Full, caret);
            await browser.PressKeyAsync("Backspace", shift: false, ct);
            Assert.Equal((value, after), await StateAsync());
        }

        // The same for Delete with the caret right before each group space (3, 7, 10): the digit after it goes.
        foreach (var (caret, value, after) in new[] { (3, "532 182 79 0", 3), (7, "532 418 79 0", 7), (10, "532 418 27 0", 10) })
        {
            await PlaceAsync(Full, caret);
            await browser.PressKeyAsync("Delete", shift: false, ct);
            Assert.Equal((value, after), await StateAsync());
        }

        // Backspace and Delete inside a group, the arrows, Home and End, and a selection deleted work as before.
        await PlaceAsync(Full, 6);
        await browser.PressKeyAsync("Backspace", shift: false, ct);
        Assert.Equal(("532 482 79 0", 5), await StateAsync());
        await browser.PressKeyAsync("Delete", shift: false, ct);
        Assert.Equal(("532 427 90", 5), await StateAsync());
        await browser.PressKeyAsync("End", shift: false, ct);
        await browser.PressKeyAsync("ArrowLeft", shift: false, ct);
        await browser.PressKeyAsync("Backspace", shift: false, ct);
        // The value needs no regrouping, so the browser leaves the caret where the digit was: right after a space, where
        // the next Backspace removes the digit before that space in one press.
        Assert.Equal(("532 427 0", 8), await StateAsync());
        await browser.PressKeyAsync("Backspace", shift: false, ct);
        Assert.Equal(("532 420", 6), await StateAsync());
        await browser.PressKeyAsync("Home", shift: false, ct);
        await browser.PressKeyAsync("Delete", shift: false, ct);
        Assert.Equal(("324 20", 0), await StateAsync());
        await browser.PressSelectAllAsync(ct);
        await browser.PressKeyAsync("Backspace", shift: false, ct);
        Assert.Equal((string.Empty, 0), await StateAsync());

        // 3. and 4. Text with 11 or more digits inserted in one go ends exactly as a paste of it does: +90, 0090 or a
        // trunk 0 removed, spaces, brackets, dashes and letters dropped, the first 10 national digits kept.
        foreach (var text in new[]
                 {
                     "5324182790123",
                     "+90 (532) 418-27-90 12",
                     "0090 532 418 27 90 999",
                     "0532 418 27 90 1",
                     "(0532) 418-27-90",
                     "+90 532 ABC 418 x 27 - 90 77"
                 })
        {
            await PasteOverAllAsync(text);
            var pasted = await StateAsync();

            await PlaceAsync(string.Empty, 0);
            await browser.InsertTextAsync(text, ct);
            Assert.Equal((Full, 13), await StateAsync());
            Assert.Equal(pasted, await StateAsync());

            await PlaceAsync("212 345 67 89", 0);
            await SelectAllAsync();
            await browser.InsertTextAsync(text, ct);
            Assert.Equal((Full, 13), await StateAsync());
        }

        // 5. A single 11th digit typed is still refused, at the end and in the middle, and the value does not change.
        await PlaceAsync(Full, 13);
        await browser.InsertTextAsync("1", ct);
        Assert.Equal((Full, 13), await StateAsync());
        await PlaceAsync(Full, 4);
        await browser.InsertTextAsync("9", ct);
        Assert.Equal((Full, 4), await StateAsync());
    }
}

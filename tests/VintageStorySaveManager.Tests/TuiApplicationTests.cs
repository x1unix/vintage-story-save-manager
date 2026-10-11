using Terminal.Gui;
using VintageStorySaveManager;

namespace VintageStorySaveManager.Tests;

public sealed class TuiApplicationTests
{
    [Theory]
    [InlineData("_Delete")]
    [InlineData("_Cancel")]
    public void DialogActionDoesNotActivateDefaultRestoreButton(string actionText)
    {
        using var dialog = new Dialog();
        var action = TuiApplication.DialogButton(actionText);
        var restore = TuiApplication.DialogButton("_Restore", isDefault: true);
        dialog.AddButton(action);
        dialog.AddButton(restore);
        var actionCount = 0;
        var restoreCount = 0;
        action.Accepting += (_, _) => actionCount++;
        restore.Accepting += (_, _) => restoreCount++;

        action.InvokeCommand(Command.Accept);

        Assert.Equal(1, actionCount);
        Assert.Equal(0, restoreCount);

        restore.InvokeCommand(Command.Accept);

        Assert.Equal(1, restoreCount);
    }
}

using System.Security;

namespace IpSwitcher.Core;

/// <summary>
/// The Task Scheduler task that starts the app elevated when the user logs on, so switching an
/// adapter never asks for UAC. Defined as XML because <c>schtasks /Create /SC ONLOGON</c> alone
/// keeps Task Scheduler's defaults: stop after 72 hours, don't start on battery, stop on battery.
/// </summary>
public static class LogonTask
{
    public const string Name = "IP Switcher";

    public static string Xml(string exePath, string user) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>Starts IP Switcher with administrator rights at logon, so changing an adapter needs no UAC prompt.</Description>
          </RegistrationInfo>
          <Triggers>
            <LogonTrigger>
              <Enabled>true</Enabled>
              <UserId>{SecurityElement.Escape(user)}</UserId>
            </LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>{SecurityElement.Escape(user)}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>5</Priority>
            <AllowHardTerminate>true</AllowHardTerminate>
            <StartWhenAvailable>false</StartWhenAvailable>
            <Enabled>true</Enabled>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>"{SecurityElement.Escape(exePath)}"</Command>
            </Exec>
          </Actions>
        </Task>
        """;
}

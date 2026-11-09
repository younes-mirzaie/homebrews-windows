/* aldetach: starts a batch file detached from the shell that runs it.
 *
 *   aldetach.exe <file.cmd>
 *
 * App Loader runs commands as SYSTEM through the phone's telnet shell. A program started there
 * belongs to that session's console and job and dies with it, and the session ends when the app
 * is suspended (screen lock, leaving the app) or updates itself. So an install runs as
 * "cmd.exe /c <file.cmd>" started from here, outside the session's console and job; this process
 * prints the new process ID and exits right away. The batch file reports through files.
 * CreateProcessW is looked up at run time because the phone's import libraries may lack it (as in
 * File Explorer's rfxfsd). */
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>

typedef BOOL(WINAPI *CreateProcessW_t)(LPCWSTR, LPWSTR, LPSECURITY_ATTRIBUTES, LPSECURITY_ATTRIBUTES,
                                       BOOL, DWORD, LPVOID, LPCWSTR, LPSTARTUPINFOW, LPPROCESS_INFORMATION);

static FARPROC find_proc(const char *name)
{
    static const WCHAR *dlls[] = {L"KERNELBASE.DLL", L"KERNEL32LEGACY.DLL", L"KERNEL32.DLL"};
    int i;
    for (i = 0; i < (int)(sizeof dlls / sizeof dlls[0]); i++) {
        HMODULE m = LoadLibraryExW(dlls[i], NULL, 0);
        FARPROC p = m ? GetProcAddress(m, name) : NULL;
        if (p) return p;
    }
    return NULL;
}

int main(int argc, char **argv)
{
    static const DWORD modes[] = {DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP | CREATE_BREAKAWAY_FROM_JOB,
                                  DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP, CREATE_NO_WINDOW};
    CreateProcessW_t create = (CreateProcessW_t)(void *)find_proc("CreateProcessW");
    WCHAR batch[MAX_PATH * 2], cmd[MAX_PATH], line[MAX_PATH * 3];
    int i;
    if (argc != 2) {
        printf("usage: aldetach <file.cmd>\n");
        return 2;
    }
    if (!create) {
        printf("aldetach: CreateProcessW not found\n");
        return 1;
    }
    if (!MultiByteToWideChar(CP_ACP, 0, argv[1], -1, batch, MAX_PATH * 2) || wcschr(batch, L'"')) {
        printf("aldetach: bad batch path\n");
        return 2;
    }
    if (!GetEnvironmentVariableW(L"ComSpec", cmd, MAX_PATH) || GetFileAttributesW(cmd) == INVALID_FILE_ATTRIBUTES)
        wcscpy(cmd, L"C:\\Windows\\System32\\cmd.exe");
    for (i = 0; i < (int)(sizeof modes / sizeof modes[0]); i++) {
        STARTUPINFOW si;
        PROCESS_INFORMATION pi;
        memset(&si, 0, sizeof si);
        si.cb = sizeof si;
        _snwprintf(line, MAX_PATH * 3, L"\"%ls\" /c \"%ls\"", cmd, batch);   /* CreateProcessW may write to it */
        line[MAX_PATH * 3 - 1] = 0;
        if (create(cmd, line, NULL, NULL, FALSE, modes[i], NULL, NULL, &si, &pi)) {
            printf("aldetach: started %lu (mode %d)\n", (unsigned long)pi.dwProcessId, i);
            fflush(stdout);
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            return 0;
        }
        printf("aldetach: mode %d failed (%lu)\n", i, (unsigned long)GetLastError());
    }
    return 1;
}

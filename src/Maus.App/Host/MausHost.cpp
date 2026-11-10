// MAUS.exe publié : lanceur natif, à la place du lanceur standard de .NET (0.8.6).
//
// Windows donne à un programme élevé l'environnement du profil (HKCU\Environment), qu'un programme sans droits peut modifier,
// et .NET lit ces variables avant le premier code de MAUS : DLL de profilage, ramasse-miettes, fichiers de trace. Ce lanceur
// les retire, puis démarre .NET dans ce même processus, exactement comme le lanceur standard (hostfxr_main_startupinfo). Il
// protège donc tous les démarrages : fenêtre, moteur après l'autorisation de Windows, benchmark, audit de la semaine.
// Dans un processus élevé, il remet aussi les variables d'emplacement à leur valeur système (reprises par SystemPaths côté .NET).
//
// Seule publication autonome : hostfxr.dll est à côté de MAUS.exe, jamais cherché ailleurs.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <wchar.h>

#pragma comment(lib, "advapi32.lib")

// Le SDK .NET remplace cette valeur par le nom de la DLL de l'application (HostWriter), comme dans son lanceur standard.
char g_appBinary[1024] = "c3ab8ff13720e8ad9047dd39466b3c8974e592c2fa383d4a3960714caef0c4f2";

namespace
{
    constexpr DWORD MaxPath = 32768;
    wchar_t g_host[MaxPath];
    wchar_t g_folder[MaxPath];
    wchar_t g_app[MaxPath];
    wchar_t g_hostfxr[MaxPath];

    using HostMain = int(__cdecl*)(int, const wchar_t**, const wchar_t*, const wchar_t*, const wchar_t*);

    bool IsRuntimeVariable(const wchar_t* name)
    {
        static const wchar_t* const prefixes[] = { L"DOTNET_", L"COMPlus_", L"CORECLR_", L"COREHOST_", L"COR_" };
        for (const wchar_t* prefix : prefixes)
        {
            if (_wcsnicmp(name, prefix, wcslen(prefix)) == 0)
            {
                return true;
            }
        }

        return false;
    }

    // GetEnvironmentStringsW rend une copie : on peut retirer des variables en la parcourant.
    bool RemoveRuntimeVariables()
    {
        wchar_t* block = GetEnvironmentStringsW();
        if (block == nullptr)
        {
            return false;
        }

        auto removed = true;
        for (const wchar_t* entry = block; *entry != L'\0'; entry += wcslen(entry) + 1)
        {
            const wchar_t* equals = wcschr(entry + 1, L'=');
            if (equals == nullptr || !IsRuntimeVariable(entry))
            {
                continue;
            }

            wchar_t name[MaxPath];
            removed = wcsncpy_s(name, entry, static_cast<size_t>(equals - entry)) == 0 && SetEnvironmentVariableW(name, nullptr) != 0 && removed;
        }

        FreeEnvironmentStringsW(block);
        return removed;
    }

    // En cas de doute, le processus est traité comme élevé : ces valeurs conviennent à tout processus.
    bool IsElevated()
    {
        HANDLE token = nullptr;
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token))
        {
            return true;
        }

        TOKEN_ELEVATION elevation{};
        DWORD size = 0;
        const BOOL read = GetTokenInformation(token, TokenElevation, &elevation, sizeof(elevation), &size);
        CloseHandle(token);
        return read == FALSE || elevation.TokenIsElevated != 0;
    }

    // Le lecteur et le dossier de Windows viennent du système, jamais de l'environnement.
    bool ResetSystemVariables()
    {
        wchar_t windows[MAX_PATH];
        const UINT length = GetSystemWindowsDirectoryW(windows, MAX_PATH);
        if (length < 3 || length >= MAX_PATH || windows[1] != L':')
        {
            return false;
        }

        const wchar_t drive[3] = { windows[0], L':', L'\0' };
        wchar_t programData[MAX_PATH + 16];
        wchar_t temp[MAX_PATH + 16];
        swprintf_s(programData, L"%s\\ProgramData", drive);
        swprintf_s(temp, L"%s\\Temp", windows);
        return SetEnvironmentVariableW(L"SystemDrive", drive) != 0
            && SetEnvironmentVariableW(L"SystemRoot", windows) != 0
            && SetEnvironmentVariableW(L"windir", windows) != 0
            && SetEnvironmentVariableW(L"ProgramData", programData) != 0
            && SetEnvironmentVariableW(L"ALLUSERSPROFILE", programData) != 0
            && SetEnvironmentVariableW(L"TEMP", temp) != 0
            && SetEnvironmentVariableW(L"TMP", temp) != 0;
    }

    int Fail(const wchar_t* message)
    {
        MessageBoxW(nullptr, message, L"MAUS", MB_OK | MB_ICONERROR);
        return 1;
    }
}

int wmain(int argc, wchar_t* argv[])
{
    // Jamais de démarrage de .NET avec une variable qu'on n'a pas pu retirer.
    if (!RemoveRuntimeVariables() || (IsElevated() && !ResetSystemVariables()))
    {
        return Fail(L"MAUS n'a pas pu préparer son démarrage. / MAUS could not prepare its start.");
    }

    const DWORD length = GetModuleFileNameW(nullptr, g_host, MaxPath);
    wchar_t name[MAX_PATH];
    if (length == 0 || length >= MaxPath || MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, g_appBinary, -1, name, MAX_PATH) == 0
        || wcscmp(name, L"c3ab8ff13720e8ad9047dd39466b3c8974e592c2fa383d4a3960714caef0c4f2") == 0)
    {
        return Fail(L"Ce MAUS.exe est incomplet : réinstallez MAUS. / This MAUS.exe is incomplete: reinstall MAUS.");
    }

    wcscpy_s(g_folder, g_host);
    wchar_t* slash = wcsrchr(g_folder, L'\\');
    if (slash == nullptr)
    {
        return Fail(L"Ce MAUS.exe est incomplet : réinstallez MAUS. / This MAUS.exe is incomplete: reinstall MAUS.");
    }

    *slash = L'\0';
    swprintf_s(g_app, L"%s\\%s", g_folder, name);
    swprintf_s(g_hostfxr, L"%s\\hostfxr.dll", g_folder);

    // Chemin complet : hostfxr.dll n'est jamais cherché hors du dossier de MAUS.
    HMODULE hostfxr = LoadLibraryExW(g_hostfxr, nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    auto start = hostfxr == nullptr ? nullptr : reinterpret_cast<HostMain>(reinterpret_cast<void*>(GetProcAddress(hostfxr, "hostfxr_main_startupinfo")));
    if (start == nullptr)
    {
        return Fail(L"MAUS ne trouve pas ses fichiers : réinstallez MAUS. / MAUS cannot find its files: reinstall MAUS.");
    }

    const int code = start(argc, const_cast<const wchar_t**>(argv), g_host, g_folder, g_app);

    // 0x800080xx : .NET n'a pas pu démarrer. Tout autre code est celui de MAUS (moteur, benchmark) et revient tel quel.
    if ((static_cast<unsigned int>(code) & 0xFFFFFF00u) == 0x80008000u)
    {
        wchar_t message[160];
        swprintf_s(message, L"MAUS n'a pas pu démarrer (code 0x%08X) : réinstallez MAUS. / MAUS could not start: reinstall MAUS.", static_cast<unsigned int>(code));
        MessageBoxW(nullptr, message, L"MAUS", MB_OK | MB_ICONERROR);
    }

    return code;
}

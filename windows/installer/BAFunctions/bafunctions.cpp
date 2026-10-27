// BAFunctions.dll: what the Role page (Bundle/Theme.xml) needs that WixStdBA's theme cannot do by
// itself.
//
//   - The list of this machine's IPv4 addresses, for a core or node to be reached at: the theme's
//     Combobox is bound to no variable and nothing fills it. The one chosen goes to Address on OK.
//   - For a node, its core. The page looks for one on this machine's networks (every address of each
//     /24, GET /api/deployment on the web's port, the way a browser would; no broadcast), lists what
//     answered, and says whether that core takes a new node without a password. On OK it asks the
//     core for the node's package (POST /api/deployment/join), with an administrator's user name and
//     password if the core has closed enrollment, and hands the package to the installation as
//     NodePackage. The password goes nowhere else: not into the MSI, whose command line ends up in
//     the installation log, and the edit box is emptied once the core has answered.
//
// The rest is checked once more by Install-TaVms.ps1, which is what unattended installs run: they
// never load the theme, so none of this runs for them.

#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <iphlpapi.h>
#include <winhttp.h>

#pragma warning(push)
#pragma warning(disable:4458) // declaration of 'xxx' hides class member
#include <gdiplus.h>
#pragma warning(pop)

#include <msiquery.h>
#include <objbase.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <stdlib.h>
#include <strsafe.h>
#include <CommCtrl.h>

#include "dutil.h"
#include "dictutil.h"
#include "fileutil.h"
#include "pathutil.h"
#include "strutil.h"
#include "regutil.h"

#include "BootstrapperApplicationBase.h"
#include "balutil.h"

#include "BAFunctions.h"
#include "IBAFunctions.h"
#include "BalBaseBAFunctions.h"
#include "BalBaseBAFunctionsProc.h"

#include <atomic>
#include <cwctype>
#include <map>
#include <mutex>
#include <set>
#include <string>
#include <thread>
#include <vector>

namespace
{
  // The names in Theme.xml.
  constexpr LPCWSTR AddressListControl = L"AddressList";
  constexpr LPCWSTR CoreListControl = L"CoreList";
  constexpr LPCWSTR CoreAddressControl = L"CoreAddress";
  constexpr LPCWSTR NodeNameControl = L"NodeName";
  constexpr LPCWSTR CoreStatusControl = L"CoreStatus";
  constexpr LPCWSTR JoinUserControl = L"JoinUser";
  constexpr LPCWSTR JoinPasswordControl = L"JoinPassword";
  constexpr LPCWSTR MediaMtxControl = L"NodeMediaMtx";
  constexpr LPCWSTR OkButtonControl = L"RoleOkButton";
  constexpr LPCWSTR NodeRadioControl = L"RoleNode";

  // WEB_VMS_PORT, which no installation changes; another one is given as address:port.
  constexpr INTERNET_PORT DefaultWebPort = 5134;
  constexpr int ScanThreads = 32;
  constexpr DWORD ScanTimeoutMs = 400;
  constexpr size_t MaxResponseBytes = 4 << 20;

  bool IsNamed(LPCWSTR name, LPCWSTR expected)
  {
    return name && CSTR_EQUAL == ::CompareStringW(LOCALE_INVARIANT, 0, name, -1, expected, -1);
  }

  std::wstring GetVariable(LPCWSTR name)
  {
    LPWSTR value = nullptr;
    std::wstring result;
    if (SUCCEEDED(BalGetStringVariable(name, &value)) && value)
      result = value;
    ReleaseStr(value);
    return result;
  }

  std::wstring WindowText(HWND window)
  {
    if (!window)
      return {};
    std::wstring text(static_cast<size_t>(::GetWindowTextLengthW(window)) + 1, L'\0');
    text.resize(static_cast<size_t>(::GetWindowTextW(window, text.data(), static_cast<int>(text.size()))));
    return text;
  }

  // From another thread than the window's: never waits for long, so that a UI thread waiting for that
  // thread to end cannot deadlock with it (GetWindowText and SetWindowText would wait for ever).
  void SetTextFromWorker(HWND window, const std::wstring& text)
  {
    if (window)
      ::SendMessageTimeoutW(window, WM_SETTEXT, 0, reinterpret_cast<LPARAM>(text.c_str()),
        SMTO_BLOCK | SMTO_ABORTIFHUNG, 1000, nullptr);
  }

  bool IsEmptyFromWorker(HWND window)
  {
    DWORD_PTR length = 0;
    return !window || !::SendMessageTimeoutW(window, WM_GETTEXTLENGTH, 0, 0, SMTO_BLOCK | SMTO_ABORTIFHUNG, 1000, &length)
      || length == 0;
  }

  std::wstring SelectedItem(HWND combobox)
  {
    const auto index = ::SendMessageW(combobox, CB_GETCURSEL, 0, 0);
    if (index == CB_ERR)
      return {};
    const auto length = ::SendMessageW(combobox, CB_GETLBTEXTLEN, static_cast<WPARAM>(index), 0);
    if (length == CB_ERR)
      return {};
    std::wstring text(static_cast<size_t>(length) + 1, L'\0');
    ::SendMessageW(combobox, CB_GETLBTEXT, static_cast<WPARAM>(index), reinterpret_cast<LPARAM>(text.data()));
    text.resize(static_cast<size_t>(length));
    return text;
  }

  std::string ToUtf8(const std::wstring& text)
  {
    if (text.empty())
      return {};
    const int size = ::WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
    std::string result(static_cast<size_t>(size), '\0');
    ::WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), result.data(), size, nullptr, nullptr);
    return result;
  }

  std::wstring FromUtf8(const std::string& text)
  {
    if (text.empty())
      return {};
    const int size = ::MultiByteToWideChar(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), nullptr, 0);
    std::wstring result(static_cast<size_t>(size), L'\0');
    ::MultiByteToWideChar(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), result.data(), size);
    return result;
  }

  std::string JsonString(const std::wstring& value)
  {
    std::string out = "\"";
    for (const char c : ToUtf8(value))
    {
      switch (c)
      {
      case '"': out += "\\\""; break;
      case '\\': out += "\\\\"; break;
      default:
        if (static_cast<unsigned char>(c) < 0x20)
        {
          char escaped[8];
          ::StringCchPrintfA(escaped, ARRAYSIZE(escaped), "\\u%04x", static_cast<unsigned char>(c));
          out += escaped;
        }
        else
          out += c;
      }
    }
    return out + "\"";
  }

  // The flat objects web_srv answers with (System.Text.Json, no spaces): enough to read a field of
  // /api/deployment or the "error" of a refusal without a JSON library in the wizard.
  bool JsonTrue(const std::string& json, const char* key)
  {
    return json.find(std::string("\"") + key + "\":true") != std::string::npos;
  }

  std::wstring JsonField(const std::string& json, const char* key)
  {
    const std::string marker = std::string("\"") + key + "\":\"";
    const auto start = json.find(marker);
    if (start == std::string::npos)
      return {};
    std::string value;
    for (auto i = start + marker.size(); i < json.size() && json[i] != '"'; ++i)
    {
      if (json[i] == '\\' && i + 1 < json.size())
        ++i;
      value += json[i];
    }
    return FromUtf8(value);
  }

  struct HttpAnswer
  {
    DWORD status = 0;
    std::string body;
  };

  // "a.b.c.d" or "a.b.c.d:port".
  void SplitHostPort(const std::wstring& text, std::wstring& host, INTERNET_PORT& port)
  {
    const auto colon = text.rfind(L':');
    host = colon == std::wstring::npos ? text : text.substr(0, colon);
    port = colon == std::wstring::npos ? DefaultWebPort : static_cast<INTERNET_PORT>(_wtoi(text.c_str() + colon + 1));
    if (port == 0)
      port = DefaultWebPort;
  }

  // Whether anything takes a TCP connection there within the time given. WinHTTP's own connect
  // timeout does not hold for an address nobody has: it waits out ARP or the SYN's retransmissions,
  // about 4 s, and the search over a mostly empty /24 took close to a minute. Winsock must be up.
  bool Reachable(const std::wstring& hostPort, DWORD timeoutMs)
  {
    std::wstring host;
    INTERNET_PORT port = 0;
    SplitHostPort(hostPort, host, port);
    sockaddr_in to{};
    to.sin_family = AF_INET;
    to.sin_port = ::htons(port);
    if (::InetPtonW(AF_INET, host.c_str(), &to.sin_addr) != 1)
      return true; // a name rather than an address: left to WinHTTP
    const SOCKET s = ::socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (s == INVALID_SOCKET)
      return true;
    u_long nonBlocking = 1;
    bool connected = false;
    if (::ioctlsocket(s, FIONBIO, &nonBlocking) != 0)
      connected = true; // cannot bound the wait here: left to WinHTTP
    else if (::connect(s, reinterpret_cast<const sockaddr*>(&to), sizeof(to)) == 0)
      connected = true;
    else if (::WSAGetLastError() == WSAEWOULDBLOCK)
    {
      // A refused or failed connection is reported in the exception set, never as writable.
      fd_set writable, failed;
      FD_ZERO(&writable);
      FD_SET(s, &writable);
      FD_ZERO(&failed);
      FD_SET(s, &failed);
      timeval wait{ static_cast<long>(timeoutMs / 1000), static_cast<long>(timeoutMs % 1000) * 1000 };
      connected = ::select(0, nullptr, &writable, &failed, &wait) > 0 && FD_ISSET(s, &writable);
    }
    ::closesocket(s);
    return connected;
  }

  // One plain HTTP request to a web of this product, never through a proxy: the core is on the LAN.
  bool Http(const std::wstring& hostPort, LPCWSTR verb, LPCWSTR path, const std::string& body, DWORD timeoutMs, HttpAnswer& answer)
  {
    std::wstring host;
    INTERNET_PORT port = 0;
    SplitHostPort(hostPort, host, port);

    struct Handle
    {
      HINTERNET h = nullptr;
      ~Handle() { if (h) ::WinHttpCloseHandle(h); }
    };
    Handle session{ ::WinHttpOpen(L"TreeAlarm VMS setup", WINHTTP_ACCESS_TYPE_NO_PROXY, WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0) };
    if (!session.h)
      return false;
    ::WinHttpSetTimeouts(session.h, static_cast<int>(timeoutMs), static_cast<int>(timeoutMs),
      static_cast<int>(timeoutMs), static_cast<int>(timeoutMs) * 4);
    Handle connection{ ::WinHttpConnect(session.h, host.c_str(), port, 0) };
    if (!connection.h)
      return false;
    Handle request{ ::WinHttpOpenRequest(connection.h, verb, path, nullptr, WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, 0) };
    if (!request.h)
      return false;
    const LPCWSTR headers = body.empty() ? WINHTTP_NO_ADDITIONAL_HEADERS : L"Content-Type: application/json\r\n";
    if (!::WinHttpSendRequest(request.h, headers, body.empty() ? 0 : static_cast<DWORD>(-1L),
          body.empty() ? WINHTTP_NO_REQUEST_DATA : const_cast<char*>(body.data()),
          static_cast<DWORD>(body.size()), static_cast<DWORD>(body.size()), 0)
        || !::WinHttpReceiveResponse(request.h, nullptr))
      return false;

    DWORD size = sizeof(answer.status);
    if (!::WinHttpQueryHeaders(request.h, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
          WINHTTP_HEADER_NAME_BY_INDEX, &answer.status, &size, WINHTTP_NO_HEADER_INDEX))
      return false;
    answer.body.clear();
    for (DWORD available = 0; ::WinHttpQueryDataAvailable(request.h, &available) && available > 0;)
    {
      std::string chunk(available, '\0');
      DWORD read = 0;
      if (!::WinHttpReadData(request.h, chunk.data(), available, &read) || read == 0)
        break;
      answer.body.append(chunk.data(), read);
      if (answer.body.size() > MaxResponseBytes)
        return false;
    }
    return true;
  }

  // lower-case letters, digits and '-', not at either end, not "core": TaVmsHost's Role.cs.
  bool IsNodeName(const std::wstring& name)
  {
    if (name.empty() || name.size() > 40 || name == L"core" || name.front() == L'-' || name.back() == L'-')
      return false;
    for (const wchar_t c : name)
    {
      if (!((c >= L'a' && c <= L'z') || (c >= L'0' && c <= L'9') || c == L'-'))
        return false;
    }
    return true;
  }

  // This computer's name, as a node's: what Install-TaVms.ps1 would choose.
  std::wstring DefaultNodeName()
  {
    wchar_t computer[MAX_COMPUTERNAME_LENGTH + 1] = {};
    DWORD size = ARRAYSIZE(computer);
    if (!::GetComputerNameW(computer, &size))
      return {};
    std::wstring name;
    for (DWORD i = 0; i < size; ++i)
    {
      const wchar_t c = static_cast<wchar_t>(::towlower(computer[i]));
      name += (c >= L'a' && c <= L'z') || (c >= L'0' && c <= L'9') ? c : L'-';
    }
    while (!name.empty() && name.front() == L'-') name.erase(name.begin());
    while (!name.empty() && name.back() == L'-') name.pop_back();
    return name;
  }

  // The addresses of the interfaces that are up, without loopback and link-local ones (169.254/16,
  // what Windows makes up when DHCP does not answer). Those of an interface with a default gateway
  // come first: that is usually the LAN, the others are VPNs and virtual switches.
  std::vector<std::wstring> MachineIPv4Addresses()
  {
    std::vector<BYTE> buffer;
    ULONG size = 16 * 1024;
    ULONG result = ERROR_BUFFER_OVERFLOW;
    for (int attempt = 0; attempt < 3 && result == ERROR_BUFFER_OVERFLOW; ++attempt)
    {
      buffer.resize(size);
      result = ::GetAdaptersAddresses(AF_INET,
        GAA_FLAG_SKIP_ANYCAST | GAA_FLAG_SKIP_MULTICAST | GAA_FLAG_SKIP_DNS_SERVER | GAA_FLAG_INCLUDE_GATEWAYS,
        nullptr, reinterpret_cast<PIP_ADAPTER_ADDRESSES>(buffer.data()), &size);
    }
    if (result != NO_ERROR)
    {
      BalLog(BOOTSTRAPPER_LOG_LEVEL_ERROR, "Role page: GetAdaptersAddresses failed with %u", result);
      return {};
    }

    std::vector<std::wstring> withGateway, others;
    for (auto adapter = reinterpret_cast<PIP_ADAPTER_ADDRESSES>(buffer.data()); adapter; adapter = adapter->Next)
    {
      if (adapter->OperStatus != IfOperStatusUp || adapter->IfType == IF_TYPE_SOFTWARE_LOOPBACK)
        continue;
      for (auto unicast = adapter->FirstUnicastAddress; unicast; unicast = unicast->Next)
      {
        const auto* address = reinterpret_cast<const sockaddr_in*>(unicast->Address.lpSockaddr);
        if (address->sin_family != AF_INET)
          continue;
        const auto first = address->sin_addr.S_un.S_un_b.s_b1;
        const auto second = address->sin_addr.S_un.S_un_b.s_b2;
        if (first == 127 || (first == 169 && second == 254))
          continue;
        wchar_t text[INET_ADDRSTRLEN] = {};
        if (!::InetNtopW(AF_INET, &address->sin_addr, text, INET_ADDRSTRLEN))
          continue;
        (adapter->FirstGatewayAddress ? withGateway : others).emplace_back(text);
      }
    }
    withGateway.insert(withGateway.end(), others.begin(), others.end());
    return withGateway;
  }
}

class CTaVmsBAFunctions : public CBalBaseBAFunctions
{
public:
  explicit CTaVmsBAFunctions(HMODULE hModule) : CBalBaseBAFunctions(hModule) {}

  ~CTaVmsBAFunctions()
  {
    StopScan();
  }

  // Before the DLL goes: the search for a core may still be running. Also from BAFunctionsDestroy,
  // which is certain to run, whatever the base class does about destructors.
  void StopScan()
  {
    m_stopping = true;
    if (m_scan.joinable())
      m_scan.join();
  }

  // InstallFolder without a trailing '\', whichever way it came. Read back from the registry on an
  // upgrade it has one, since the MSI writes [INSTALLFOLDER] there; so may one given on the command
  // line. The Open button's "[InstallFolder]\TreeAlarm VMS.url" (Bundle.wxs) then doubles it, and
  // explorer.exe, which cannot parse that path, opens Documents instead of the browser. The default,
  // "[ProgramFiles64Folder]TreeAlarm VMS", has none and is left as it is, unformatted.
  STDMETHODIMP OnDetectComplete(HRESULT hrStatus, BOOL fEligibleForCleanup) override
  {
    auto folder = GetVariable(L"InstallFolder");
    if (folder.size() > 3 && (folder.back() == L'\\' || folder.back() == L'/'))
    {
      while (folder.size() > 3 && (folder.back() == L'\\' || folder.back() == L'/'))
        folder.pop_back();
      BalSetStringVariable(L"InstallFolder", folder.c_str(), FALSE);
    }
    return __super::OnDetectComplete(hrStatus, fEligibleForCleanup);
  }

  // Role= given over the same version already installed — a standalone installation asked to become
  // the core: Burn sees the package present, plans nothing and exits 0, so Install-TaVms.ps1 never
  // runs and the machine silently stays what it was. Repairing the package runs it, with the role.
  // An upgrade runs it anyway, and without Role= nothing is asked of it.
  STDMETHODIMP OnPlanPackageBegin(LPCWSTR wzPackageId, BOOTSTRAPPER_PACKAGE_STATE state, BOOL fCached,
    BOOTSTRAPPER_PACKAGE_CONDITION_RESULT installCondition, BOOTSTRAPPER_PACKAGE_CONDITION_RESULT repairCondition,
    BOOTSTRAPPER_REQUEST_STATE recommendedState, BOOTSTRAPPER_CACHE_TYPE recommendedCacheType,
    BOOTSTRAPPER_REQUEST_STATE* pRequestedState, BOOTSTRAPPER_CACHE_TYPE* pRequestedCacheType, BOOL* pfCancel) override
  {
    if (IsNamed(wzPackageId, L"TaVms") && state == BOOTSTRAPPER_PACKAGE_STATE_PRESENT
      && *pRequestedState == BOOTSTRAPPER_REQUEST_STATE_PRESENT && !GetVariable(L"Role").empty())
    {
      BalLog(BOOTSTRAPPER_LOG_LEVEL_STANDARD, "Role given over the same version: repairing TaVms to apply it");
      *pRequestedState = BOOTSTRAPPER_REQUEST_STATE_REPAIR;
    }
    return __super::OnPlanPackageBegin(wzPackageId, state, fCached, installCondition, repairCondition,
      recommendedState, recommendedCacheType, pRequestedState, pRequestedCacheType, pfCancel);
  }

  STDMETHODIMP OnThemeControlLoaded(LPCWSTR wzName, WORD wId, HWND hWnd, BOOL* pfProcessed) override
  {
    if (IsNamed(wzName, AddressListControl))
    {
      m_addressList = hWnd;
      FillAddressList();
      *pfProcessed = TRUE;
    }
    else if (IsNamed(wzName, CoreListControl))
    {
      m_coreList = hWnd;
      *pfProcessed = TRUE;
    }
    else if (IsNamed(wzName, CoreAddressControl))
      m_coreAddress = hWnd;
    else if (IsNamed(wzName, NodeNameControl))
    {
      m_nodeName = hWnd;
      // Into the variable as well: the theme fills the box from the variable of its name.
      if (GetVariable(L"NodeName").empty())
      {
        const auto name = DefaultNodeName();
        BalSetStringVariable(L"NodeName", name.c_str(), FALSE);
        ::SetWindowTextW(hWnd, name.c_str());
      }
    }
    else if (IsNamed(wzName, CoreStatusControl))
      m_coreStatus = hWnd;
    else if (IsNamed(wzName, JoinUserControl))
      m_joinUser = hWnd;
    else if (IsNamed(wzName, JoinPasswordControl))
    {
      m_joinPassword = hWnd;
      ::SendMessageW(hWnd, EM_SETPASSWORDCHAR, static_cast<WPARAM>(L'\x25CF'), 0);
    }
    else if (IsNamed(wzName, MediaMtxControl))
      m_mediaMtx = hWnd;
    return __super::OnThemeControlLoaded(wzName, wId, hWnd, pfProcessed);
  }

  STDMETHODIMP OnThemeControlWmCommand(
    WPARAM wParam, LPCWSTR wzName, WORD wId, HWND hWnd, BOOL* pfProcessed, LRESULT* plResult) override
  {
    // Looked for only once the machine is to be a node: a standalone installation has no business
    // asking every address of the network.
    if (HIWORD(wParam) == BN_CLICKED && IsNamed(wzName, NodeRadioControl))
      StartScan();
    else if (HIWORD(wParam) == CBN_SELCHANGE && IsNamed(wzName, CoreListControl))
    {
      ChooseFoundCore();
      *pfProcessed = TRUE;
      *plResult = 0;
    }
    else if (HIWORD(wParam) == BN_CLICKED && IsNamed(wzName, OkButtonControl))
    {
      SaveAddress();
      // Marked processed only when the node could not join: the page stays, with the reason on it.
      // Otherwise the button's own action, back to the Install page, runs.
      if (GetVariable(L"Role") == L"node" && !JoinCore())
      {
        *pfProcessed = TRUE;
        *plResult = 0;
      }
    }
    return __super::OnThemeControlWmCommand(wParam, wzName, wId, hWnd, pfProcessed, plResult);
  }

private:
  // Selects the address already in Address if it is in the list (Address= on the command line, or
  // the page opened a second time), the first one otherwise.
  void FillAddressList()
  {
    const auto addresses = MachineIPv4Addresses();
    const auto wanted = GetVariable(L"Address");
    int selected = addresses.empty() ? -1 : 0;
    for (size_t i = 0; i < addresses.size(); ++i)
    {
      ::SendMessageW(m_addressList, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(addresses[i].c_str()));
      if (addresses[i] == wanted)
        selected = static_cast<int>(i);
      BalLog(BOOTSTRAPPER_LOG_LEVEL_STANDARD, "Role page: offering address %ls", addresses[i].c_str());
    }
    if (selected >= 0)
      ::SendMessageW(m_addressList, CB_SETCURSEL, static_cast<WPARAM>(selected), 0);
  }

  // Only for a core or a node: a standalone installation is given no address, as before the page.
  void SaveAddress()
  {
    const auto role = GetVariable(L"Role");
    if (role != L"core" && role != L"node")
      return;
    const auto text = SelectedItem(m_addressList);
    if (text.empty())
      return;
    BalSetStringVariable(L"Address", text.c_str(), FALSE);
    BalLog(BOOTSTRAPPER_LOG_LEVEL_STANDARD, "Role page: %ls at %ls", role.c_str(), text.c_str());
  }

  // Every address of the /24 of each of this machine's addresses, asked at once by a few threads.
  // A /24 because a LAN's mask may be wider than anyone would wait for; a core outside it is typed in.
  void StartScan()
  {
    if (m_scan.joinable())
      return;
    ::SetWindowTextW(m_coreStatus, L"Looking for a core on this computer's networks...");
    m_scan = std::thread([this, own = MachineIPv4Addresses()] { Scan(own); });
  }

  void Scan(const std::vector<std::wstring>& own)
  {
    std::vector<std::wstring> targets;
    std::set<std::wstring> seen;
    for (const auto& address : own)
    {
      const auto prefix = address.substr(0, address.rfind(L'.') + 1);
      for (int host = 1; host < 255; ++host)
      {
        auto target = prefix + std::to_wstring(host);
        if (seen.insert(target).second)
          targets.push_back(std::move(target));
      }
    }

    std::atomic<size_t> next{ 0 };
    std::mutex lock;
    // The core's address, and its machine name once the core itself has answered: a node's web
    // answers too, with its core's address.
    std::map<std::wstring, std::wstring> cores;
    WSADATA wsa{};
    const bool winsock = ::WSAStartup(MAKEWORD(2, 2), &wsa) == 0;
    std::vector<std::thread> workers;
    for (int i = 0; i < ScanThreads; ++i)
    {
      workers.emplace_back([&]
      {
        for (size_t at; !m_stopping && (at = next++) < targets.size();)
        {
          HttpAnswer answer;
          if ((winsock && !Reachable(targets[at], ScanTimeoutMs))
              || !Http(targets[at], L"GET", L"/api/deployment", {}, ScanTimeoutMs, answer)
              || answer.status != 200 || !JsonTrue(answer.body, "distributed"))
            continue;
          const auto core = JsonField(answer.body, "coreAddress");
          if (core.empty())
            continue;
          std::lock_guard guard(lock);
          auto& machine = cores[core];
          if (targets[at] == core)
            machine = JsonField(answer.body, "machine");
        }
      });
    }
    for (auto& worker : workers)
      worker.join();
    if (winsock)
      ::WSACleanup();
    if (m_stopping)
      return;

    for (const auto& [core, machine] : cores)
    {
      const auto item = machine.empty() ? core : core + L"  (" + machine + L")";
      ::SendMessageTimeoutW(m_coreList, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(item.c_str()),
        SMTO_BLOCK | SMTO_ABORTIFHUNG, 1000, nullptr);
      BalLog(BOOTSTRAPPER_LOG_LEVEL_STANDARD, "Role page: found the core at %ls", item.c_str());
    }
    if (cores.empty())
    {
      SetTextFromWorker(m_coreStatus, L"No core answered on this computer's networks: type its address below.");
      return;
    }
    if (cores.size() == 1 && IsEmptyFromWorker(m_coreAddress))
    {
      ::SendMessageTimeoutW(m_coreList, CB_SETCURSEL, 0, 0, SMTO_BLOCK | SMTO_ABORTIFHUNG, 1000, nullptr);
      SetTextFromWorker(m_coreAddress, cores.begin()->first);
      SetTextFromWorker(m_coreStatus, Enrollment(cores.begin()->first));
      return;
    }
    SetTextFromWorker(m_coreStatus, std::to_wstring(cores.size()) + L" cores answered: choose this node's.");
  }

  void ChooseFoundCore()
  {
    const auto item = SelectedItem(m_coreList);
    const auto core = item.substr(0, item.find(L' '));
    ::SetWindowTextW(m_coreAddress, core.c_str());
    ::SetWindowTextW(m_coreStatus, Enrollment(core).c_str());
  }

  // What the core says about taking this node, for the line under the core.
  static std::wstring Enrollment(const std::wstring& core)
  {
    HttpAnswer answer;
    if (!Http(core, L"GET", L"/api/deployment", {}, 3000, answer) || answer.status != 200)
      return L"The core at " + core + L" does not answer.";
    if (!JsonTrue(answer.body, "distributed"))
      return core + L" is not the core of a deployment over several computers.";
    return JsonTrue(answer.body, "enrollmentOpen")
      ? L"The core takes new nodes now: no password is needed."
      : L"The core has closed enrollment: an administrator's user name and password are needed.";
  }

  // Asks the core for this node's package and gives it to the installation. False, with a message
  // box saying why, when the node cannot join.
  bool JoinCore()
  {
    const auto core = WindowText(m_coreAddress);
    const auto name = WindowText(m_nodeName);
    if (core.empty())
      return Refuse(L"Choose this node's core in the list, or type its address.");
    if (!IsNodeName(name))
      return Refuse(L"The node's name may hold lower-case letters, digits and '-', not at either end, and cannot be 'core'.");

    const auto mediaMtx = ::SendMessageW(m_mediaMtx, BM_GETCHECK, 0, 0) == BST_CHECKED;
    const auto body = "{\"name\":" + JsonString(name) + ",\"address\":" + JsonString(GetVariable(L"Address"))
      + ",\"mediaMtx\":" + (mediaMtx ? "true" : "false")
      + ",\"username\":" + JsonString(WindowText(m_joinUser)) + ",\"password\":" + JsonString(WindowText(m_joinPassword)) + "}";
    HttpAnswer answer;
    const bool answered = Http(core, L"POST", L"/api/deployment/join", body, 15000, answer);
    // Emptied whatever the answer: the theme saves the box into a variable when the page is left.
    ::SetWindowTextW(m_joinPassword, L"");
    if (!answered)
      return Refuse(L"The core at " + core + L" does not answer.");
    if (answer.status != 200)
    {
      auto reason = JsonField(answer.body, "error");
      if (reason.empty())
        reason = L"HTTP " + std::to_wstring(answer.status);
      BalLog(BOOTSTRAPPER_LOG_LEVEL_STANDARD, "Role page: the core at %ls refused node '%ls': %ls", core.c_str(), name.c_str(), reason.c_str());
      return Refuse(L"The core refused this node: " + reason);
    }

    wchar_t temp[MAX_PATH + 1] = {};
    ::GetTempPathW(ARRAYSIZE(temp), temp);
    const auto path = std::wstring(temp) + L"TreeAlarm-node-" + name + L".tar.gz";
    const HANDLE file = ::CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    DWORD written = 0;
    const bool saved = file != INVALID_HANDLE_VALUE
      && ::WriteFile(file, answer.body.data(), static_cast<DWORD>(answer.body.size()), &written, nullptr)
      && written == answer.body.size();
    if (file != INVALID_HANDLE_VALUE)
      ::CloseHandle(file);
    if (!saved)
      return Refuse(L"The node's package could not be saved in " + path + L".");

    // Install-TaVms.ps1 joins with it, and deletes it once the node has joined.
    BalSetStringVariable(L"NodePackage", path.c_str(), FALSE);
    BalLog(BOOTSTRAPPER_LOG_LEVEL_STANDARD, "Role page: node '%ls' enrolled at the core at %ls", name.c_str(), core.c_str());
    return true;
  }

  bool Refuse(const std::wstring& reason)
  {
    ::MessageBoxW(m_hwndParent, reason.c_str(), L"TreeAlarm VMS", MB_OK | MB_ICONWARNING);
    return false;
  }

  HWND m_addressList = nullptr;
  HWND m_coreList = nullptr;
  HWND m_coreAddress = nullptr;
  HWND m_nodeName = nullptr;
  HWND m_coreStatus = nullptr;
  HWND m_joinUser = nullptr;
  HWND m_joinPassword = nullptr;
  HWND m_mediaMtx = nullptr;
  std::atomic<bool> m_stopping{ false };
  std::thread m_scan;
};

static HINSTANCE vhInstance = nullptr;
static CTaVmsBAFunctions* vFunctions = nullptr;

extern "C" BOOL WINAPI DllMain(HINSTANCE hInstance, DWORD dwReason, LPVOID /*pvReserved*/)
{
  switch (dwReason)
  {
  case DLL_PROCESS_ATTACH:
    ::DisableThreadLibraryCalls(hInstance);
    vhInstance = hInstance;
    break;
  case DLL_PROCESS_DETACH:
    vhInstance = nullptr;
    break;
  }
  return TRUE;
}

extern "C" HRESULT WINAPI BAFunctionsCreate(
  const BA_FUNCTIONS_CREATE_ARGS* pArgs, BA_FUNCTIONS_CREATE_RESULTS* pResults)
{
  // Required before any Bal* call, logging included.
  BalInitialize(pArgs->pEngine);

  auto* functions = new (std::nothrow) CTaVmsBAFunctions(vhInstance);
  if (!functions)
    return E_OUTOFMEMORY;

  const HRESULT hr = functions->OnCreate(pArgs->pEngine, pArgs->pCommand);
  if (FAILED(hr))
  {
    BalLog(BOOTSTRAPPER_LOG_LEVEL_ERROR, "BAFunctions: OnCreate failed with 0x%x", hr);
    functions->Release();
    return hr;
  }

  pResults->pfnBAFunctionsProc = BalBaseBAFunctionsProc;
  pResults->pvBAFunctionsProcContext = functions;
  vFunctions = functions;
  return S_OK;
}

extern "C" void WINAPI BAFunctionsDestroy(
  const BA_FUNCTIONS_DESTROY_ARGS* /*pArgs*/, BA_FUNCTIONS_DESTROY_RESULTS* /*pResults*/)
{
  if (vFunctions)
    vFunctions->StopScan();
  vFunctions = nullptr;
  BalUninitialize();
}

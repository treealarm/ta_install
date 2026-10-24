// BAFunctions.dll: the two things the Role page (Bundle/Theme.xml) needs that WixStdBA's theme cannot
// do by itself. Its Combobox is bound to no variable and nothing fills it, so the list of this
// machine's IPv4 addresses is put there from here, and the one chosen is written to Address when
// the page is left with OK. And the theme has no file browse action, only a directory one, so the
// node package's Browse button is handled here too.
//
// Nothing is checked here: Install-TaVms.ps1 checks the role, the address and the package, and is
// the one place that does. Unattended installs never load the theme, so none of this runs for them.

#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <iphlpapi.h>
#include <commdlg.h>

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

#include <string>
#include <vector>

namespace
{
  // The names in Theme.xml.
  constexpr LPCWSTR AddressListControl = L"AddressList";
  constexpr LPCWSTR NodePackageControl = L"NodePackage";
  constexpr LPCWSTR BrowseButtonControl = L"NodePackageBrowseButton";
  constexpr LPCWSTR OkButtonControl = L"RoleOkButton";

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

  STDMETHODIMP OnThemeControlLoaded(LPCWSTR wzName, WORD wId, HWND hWnd, BOOL* pfProcessed) override
  {
    if (IsNamed(wzName, AddressListControl))
    {
      m_addressList = hWnd;
      FillAddressList();
      *pfProcessed = TRUE;
    }
    else if (IsNamed(wzName, NodePackageControl))
    {
      // Only remembered: the theme itself binds the edit box to the variable of its name.
      m_nodePackage = hWnd;
    }
    return __super::OnThemeControlLoaded(wzName, wId, hWnd, pfProcessed);
  }

  STDMETHODIMP OnThemeControlWmCommand(
    WPARAM wParam, LPCWSTR wzName, WORD wId, HWND hWnd, BOOL* pfProcessed, LRESULT* plResult) override
  {
    if (HIWORD(wParam) == BN_CLICKED && IsNamed(wzName, BrowseButtonControl))
    {
      BrowseNodePackage();
      *pfProcessed = TRUE;
      *plResult = 0;
    }
    else if (HIWORD(wParam) == BN_CLICKED && IsNamed(wzName, OkButtonControl))
    {
      // Not marked processed: the button's own action, back to the Install page, still runs.
      SaveAddress();
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
    const auto index = ::SendMessageW(m_addressList, CB_GETCURSEL, 0, 0);
    if (index == CB_ERR)
      return;
    const auto length = ::SendMessageW(m_addressList, CB_GETLBTEXTLEN, static_cast<WPARAM>(index), 0);
    if (length == CB_ERR)
      return;
    std::wstring text(static_cast<size_t>(length) + 1, L'\0');
    ::SendMessageW(m_addressList, CB_GETLBTEXT, static_cast<WPARAM>(index), reinterpret_cast<LPARAM>(text.data()));
    text.resize(static_cast<size_t>(length));
    BalSetStringVariable(L"Address", text.c_str(), FALSE);
    BalLog(BOOTSTRAPPER_LOG_LEVEL_STANDARD, "Role page: %ls at %ls", role.c_str(), text.c_str());
  }

  void BrowseNodePackage()
  {
    wchar_t path[4 * MAX_PATH] = {};
    const auto current = GetVariable(L"NodePackage");
    ::StringCchCopyW(path, ARRAYSIZE(path), current.c_str());

    OPENFILENAMEW dialog = {};
    dialog.lStructSize = sizeof(dialog);
    dialog.hwndOwner = m_hwndParent;
    dialog.lpstrFilter = L"Node package (*.tar.gz)\0*.tar.gz\0All files (*.*)\0*.*\0";
    dialog.lpstrFile = path;
    dialog.nMaxFile = ARRAYSIZE(path);
    dialog.Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_HIDEREADONLY | OFN_NOCHANGEDIR;
    if (!::GetOpenFileNameW(&dialog))
      return;

    // The edit box is what the theme saves into NodePackage when the page is left, so it is the one
    // that has to show the choice; the variable is set too for a page that is cancelled out of.
    ::SetWindowTextW(m_nodePackage, path);
    BalSetStringVariable(L"NodePackage", path, FALSE);
  }

  HWND m_addressList = nullptr;
  HWND m_nodePackage = nullptr;
};

static HINSTANCE vhInstance = nullptr;

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
  return S_OK;
}

extern "C" void WINAPI BAFunctionsDestroy(
  const BA_FUNCTIONS_DESTROY_ARGS* /*pArgs*/, BA_FUNCTIONS_DESTROY_RESULTS* /*pResults*/)
{
  BalUninitialize();
}

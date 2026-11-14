#include <windows.h>
#include <dia2.h>
#include <cvconst.h>
#include <stdio.h>
#include <string>
#include <initializer_list>

static void check(HRESULT hr, const wchar_t* operation) {
    if (FAILED(hr)) { fwprintf(stderr, L"%s: 0x%08X\n", operation, hr); ExitProcess(2); }
}

int wmain(int argc, wchar_t** argv) {
    if (argc != 3 && argc != 5) { fwprintf(stderr,L"Usage: Read-PdbSymbols <msdia140.dll> <pdb> [GUID age]\n"); return 1; }
    check(CoInitializeEx(nullptr,COINIT_MULTITHREADED),L"CoInitializeEx");
    HMODULE module=LoadLibraryExW(argv[1],nullptr,LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!module) { fwprintf(stderr,L"DIA load error %lu\n",GetLastError()); return 2; }
    auto getClass=reinterpret_cast<HRESULT(STDAPICALLTYPE*)(REFCLSID,REFIID,void**)>(GetProcAddress(module,"DllGetClassObject"));
    IClassFactory* factory=nullptr;
    check(getClass(__uuidof(DiaSource),IID_IClassFactory,reinterpret_cast<void**>(&factory)),L"DllGetClassObject");
    IDiaDataSource* source=nullptr;
    check(factory->CreateInstance(nullptr,__uuidof(IDiaDataSource),reinterpret_cast<void**>(&source)),L"CreateInstance");
    factory->Release();
    if (argc == 5) {
        GUID expected;
        std::wstring guidText=argv[3];
        if (guidText.empty() || guidText.front()!=L'{') guidText=L"{"+guidText+L"}";
        check(CLSIDFromString(guidText.c_str(), &expected),L"PDB GUID");
        DWORD age=wcstoul(argv[4],nullptr,10);
        check(source->loadAndValidateDataFromPdb(argv[2],&expected,0,age),L"loadAndValidateDataFromPdb");
    } else {
        check(source->loadDataFromPdb(argv[2]),L"loadDataFromPdb");
    }
    IDiaSession* session=nullptr;
    check(source->openSession(&session),L"openSession");
    IDiaSymbol* global=nullptr;
    check(session->get_globalScope(&global),L"globalScope");
    for (DWORD tag : {DWORD(SymTagPublicSymbol),DWORD(SymTagData)}) {
        IDiaEnumSymbols* entries=nullptr;
        check(global->findChildren(static_cast<enum SymTagEnum>(tag),nullptr,nsNone,&entries),L"findChildren");
        IDiaSymbol* symbol=nullptr;
        ULONG fetched=0;
        while (entries->Next(1,&symbol,&fetched)==S_OK && fetched) {
            DWORD rva=0; BSTR name=nullptr, undecorated=nullptr;
            symbol->get_relativeVirtualAddress(&rva);
            symbol->get_name(&name);
            symbol->get_undecoratedName(&undecorated);
            if (name) wprintf(L"%u\t%08X\t%s\t%s\n",tag,rva,name,undecorated ? undecorated : name);
            SysFreeString(name); SysFreeString(undecorated); symbol->Release();
        }
        entries->Release();
    }
    global->Release(); session->Release(); source->Release();
    CoUninitialize();
    return 0;
}

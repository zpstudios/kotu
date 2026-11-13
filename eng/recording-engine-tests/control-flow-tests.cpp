#include <algorithm>
#include <array>
#include <cstdint>
#include <iostream>
#include <optional>
#include <stdexcept>
#include <string>
#include <vector>
using HRESULT = std::int32_t;
using ULONGLONG = std::uint64_t;
using INT64 = std::int64_t;
using HANDLE = void*;
constexpr HRESULT S_OK=0, E_FAIL=static_cast<HRESULT>(0x80004005u), DXGI_ERROR_WAIT_TIMEOUT=static_cast<HRESULT>(0x887a0027u);
constexpr int ERROR_CANCELLED=1223, ERROR_TIMEOUT=1460, WAIT_OBJECT_0=0;
constexpr bool FALSE=false;
constexpr HRESULT HRESULT_FROM_WIN32(int value){return static_cast<HRESULT>(0x80070000u|static_cast<unsigned>(value));}
bool FAILED(HRESULT hr){return hr<0;} bool SUCCEEDED(HRESULT hr){return hr>=0;}
static ULONGLONG now=0,cancelAt=~0ull,errorAt=~0ull;
static int passed=0;
void require(bool value,const char* name){if(!value)throw std::runtime_error(name);++passed;}
ULONGLONG GetTickCount64(){return now;}
int WaitForSingleObjectEx(HANDLE,int,bool){return now>=errorAt?WAIT_OBJECT_0:258;}
namespace Concurrency {struct cancellation_token{bool is_canceled()const{return now>=cancelAt;}};}
struct CAPTURE_RESULT {HRESULT RecordingResult;std::wstring Error;CAPTURE_RESULT(HRESULT hr,std::wstring text=L""):RecordingResult(hr),Error(std::move(text)){} };
enum class RecordingSourceType{Window,Display,Camera};
struct RECORDING_SOURCE{RecordingSourceType Type;std::optional<bool> IsVideoCaptureEnabled;};
struct Texture {int refs=1;};
struct CAPTURED_FRAME{Texture* Frame{};int FrameUpdateCount{};};
struct ScreenCaptureManager {
 ULONGLONG readyAt=~0ull;bool ready=false,placeholder=false;HRESULT result=S_OK;Texture borrowed;int slices=0;INT64 largestSlice=0;
 HRESULT AcquireNextFrame(INT64,INT64 slice,const Concurrency::cancellation_token&,CAPTURED_FRAME* frame){++slices;largestSlice=std::max(largestSlice,slice);now+=static_cast<ULONGLONG>(slice/10000);if(now>=readyAt&&!placeholder)ready=true;frame->Frame=&borrowed;frame->FrameUpdateCount=ready?1:0;return result;}
 bool IsInitialFrameWriteComplete(){return ready;}
};
#include "actual-gate.inc"
struct WgcControl {
 int timeoutCalls=0,placeholders=0;bool m_HasWrittenNativeFrame=false;
 HRESULT ProcessRecordingTimeout(void*){++timeoutCalls;return DXGI_ERROR_WAIT_TIMEOUT;}
 HRESULT EmptyPool(){HRESULT hr{};void* pData=nullptr;
#include "actual-empty-pool.inc"
 return hr;}
 HRESULT PriorEmptyPool(){HRESULT hr{};void* pData=nullptr;(void)pData;
#include "prior-empty-pool.inc"
 return hr;}
 HRESULT Minimized(){
#include "actual-placeholder-guard.inc"
 ++placeholders;return S_OK;}
 void NativeCopy(const std::array<unsigned char,4>& pixels){(void)pixels;
#include "actual-native-copy-mark.inc"
 }
};
void reset(){now=0;cancelAt=errorAt=~0ull;}
int main(){try{
 WgcControl wgc;wgc.PriorEmptyPool();require(wgc.timeoutCalls==0,"prior empty pool bypass");wgc.EmptyPool();require(wgc.timeoutCalls==1,"patched empty pool recovery");require(wgc.Minimized()==DXGI_ERROR_WAIT_TIMEOUT&&wgc.placeholders==0,"initial placeholder cannot qualify");wgc.NativeCopy({0,0,0,0});require(wgc.m_HasWrittenNativeFrame,"real all-black native copy qualifies");require(wgc.Minimized()==S_OK,"post-first-frame placeholder behavior preserved");
 RECORDING_SOURCE window{RecordingSourceType::Window,{}},disabled{RecordingSourceType::Window,false},display{RecordingSourceType::Display,{}},camera{RecordingSourceType::Camera,{}};require(RequiresInitialWindowFrame({&window}),"enabled window gate");require(!RequiresInitialWindowFrame({&disabled,&display,&camera}),"other source policy unchanged");
 reset();ScreenCaptureManager absent;auto r=WaitForInitialWindowFrame(&absent,nullptr,{});require(r.RecordingResult==HRESULT_FROM_WIN32(ERROR_TIMEOUT)&&now==5000,"no first write deadline");require(absent.slices==50&&absent.largestSlice==1000000,"bounded acquisition slices");require(absent.borrowed.refs==1,"borrowed frame not released or retained");
 reset();ScreenCaptureManager black;black.readyAt=300;r=WaitForInitialWindowFrame(&black,nullptr,{});require(r.RecordingResult==S_OK&&now==300,"first real write qualifies independent of RGB");
 reset();ScreenCaptureManager placeholder;placeholder.readyAt=100;placeholder.placeholder=true;r=WaitForInitialWindowFrame(&placeholder,nullptr,{});require(r.RecordingResult==HRESULT_FROM_WIN32(ERROR_TIMEOUT),"placeholder-only deadline");
 reset();ScreenCaptureManager canceled;cancelAt=200;r=WaitForInitialWindowFrame(&canceled,nullptr,{});require(r.RecordingResult==HRESULT_FROM_WIN32(ERROR_CANCELLED)&&now==200,"cancel during acquisition");
 reset();ScreenCaptureManager before;cancelAt=0;r=WaitForInitialWindowFrame(&before,nullptr,{});require(r.RecordingResult==HRESULT_FROM_WIN32(ERROR_CANCELLED)&&before.slices==0,"pre-cancel has no acquisition");
 reset();ScreenCaptureManager broken;errorAt=100;r=WaitForInitialWindowFrame(&broken,nullptr,{});require(r.RecordingResult==E_FAIL&&now==100,"capture error before output");
 reset();ScreenCaptureManager nativeError;nativeError.result=E_FAIL;r=WaitForInitialWindowFrame(&nativeError,nullptr,{});require(r.RecordingResult==E_FAIL,"native acquire failure");
 reset();ScreenCaptureManager slow;slow.readyAt=5000;r=WaitForInitialWindowFrame(&slow,nullptr,{});require(r.RecordingResult==HRESULT_FROM_WIN32(ERROR_TIMEOUT),"late readiness cannot escape deadline");
 std::cout<<"passed="<<passed<<"; actual extracted gate/branch control flow; no native WGC execution\n";return 0;
 }catch(const std::exception& e){std::cerr<<e.what()<<"\n";return 1;}}

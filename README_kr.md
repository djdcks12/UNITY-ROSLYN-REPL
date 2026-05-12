# Unity용 Roslyn REPL

[English](README.md) | [한국어](README_kr.md)

Unity Editor 안에서 C#을 바로 실행하고, 살아 있는 오브젝트를 살펴보고, 자주 쓰는 조사 코드를 저장하고, 디버깅 중에 메서드를 임시로 패치할 수 있는 Editor 전용 도구입니다.

Roslyn REPL for Unity는 임시 Editor 스크립트를 만들거나 게임 코드 곳곳에 로그를 심기 전에 빠르게 확인하고 싶은 개발자를 위한 툴킷입니다. 창 하나에서 스니펫을 실행하고, 결과를 펼쳐 보고, 쓸 만한 코드는 저장한 뒤 바로 다음 조사로 넘어갈 수 있습니다.

## 설치

### 요구 사항

- Unity 2022.3 이상을 권장합니다.
- 이 패키지는 Editor 전용입니다. Player 빌드 결과물에 포함되지 않도록 설계되어 있습니다.

### OpenUPM으로 설치

OpenUPM CLI를 사용하면 한 줄로 설치할 수 있습니다.

```powershell
openupm add com.youngchan.roslyn-repl
```

또는 Unity에 OpenUPM scoped registry를 추가합니다.

```text
Name:   OpenUPM
URL:    https://package.openupm.com
Scope:  com.youngchan
```

그 다음 패키지를 이름으로 추가합니다.

```json
{
  "dependencies": {
    "com.youngchan.roslyn-repl": "0.7.1"
  }
}
```

패키지 페이지:

```text
https://openupm.com/packages/com.youngchan.roslyn-repl/
```

### Git URL로 설치

scoped registry를 추가하고 싶지 않다면 Git URL로 설치할 수 있습니다.

```json
"com.youngchan.roslyn-repl": "https://github.com/djdcks12/UNITY-ROSLYN-REPL.git#v0.7.1"
```

### 로컬 디스크에서 설치

1. Unity Package Manager를 엽니다.
2. `+` 버튼을 누릅니다.
3. `Add package from disk...`를 선택합니다.
4. 이 패키지의 `package.json`을 선택합니다.

또는 패키지를 아래 위치에 직접 둘 수 있습니다.

```text
Packages/com.youngchan.roslyn-repl/
```

## 첫 설정

Roslyn과 Harmony는 `Editor/Plugins/` 아래에 Editor 전용 플러그인으로 포함됩니다. 패키지를 가져온 뒤 먼저 다음 메뉴를 실행해 상태를 확인하세요.

```text
Tools / Roslyn REPL / Verify Setup
```

`Verify Setup`은 컴파일러와 패치 의존성이 제대로 로드되는지 확인하고, 중복 assembly 같은 흔한 문제를 알려줍니다. 의존성이 없거나 삭제된 경우에는 다음 메뉴로 다시 설치할 수 있습니다.

```text
Tools / Roslyn REPL / Install Roslyn DLLs
```

## 왜 쓰나요?

- Unity Editor 안에서 C# 스니펫을 바로 실행합니다.
- 씬 오브젝트, ScriptableObject, 딕셔너리, 리스트, 커스텀 클래스를 펼쳐볼 수 있는 트리로 확인합니다.
- 살아 있는 씬 오브젝트, 로드된 에셋, 자주 쓰는 싱글톤 패턴을 브라우저에서 찾습니다.
- 프로젝트별 스니펫과 실행 히스토리를 저장합니다.
- 프로젝트 namespace를 `using` 목록에 한 번 추가해 짧은 스니펫을 작성합니다.
- 이전 실행 결과를 `_`로 이어서 사용합니다.
- 실행 뒤마다 Watch 식을 평가해 가벼운 디버거 Watch처럼 씁니다.
- Harmony 기반 Runtime Method Patch로 메서드 동작을 임시 교체합니다.
- Player 빌드 의존성 없이 Editor 안에서만 동작합니다.

## 빠른 시작

1. `Tools / Roslyn REPL / Open`을 엽니다.
2. 스니펫을 입력합니다.
3. **Run**, `F5`, 또는 `Ctrl+Enter`를 누릅니다.
4. Output에서 로그와 반환값을 확인합니다.
5. 자주 쓰는 조사는 **Snippets**에 저장합니다.
6. 반복해서 보고 싶은 값은 **Watch**에 추가합니다.

```csharp
return UnityEngine.Application.unityVersion;
```

```csharp
var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();

return scene.GetRootGameObjects()
    .Select(go => new
    {
        go.name,
        active = go.activeInHierarchy,
        childCount = go.transform.childCount
    })
    .ToArray();
```

```csharp
Debug.Log("Probe started");
return UnityEngine.Object.FindObjectsByType<UnityEngine.Camera>(
    UnityEngine.FindObjectsSortMode.None);
```

## 주요 기능

### 대화형 C# REPL

메인 창에는 멀티라인 C# 편집기, 라인 번호, 커서 위치, 단축키, 컴파일 진단, 런타임 예외, 캡처된 로그, 실행 시간이 함께 표시됩니다.

Play Mode에서는 스니펫이 작은 coroutine을 통해 한 프레임 뒤에 실행됩니다. 그래서 호출 시점이 일반적인 `Button.onClick`과 같은 Player Update 흐름에 맞춰집니다. UI, 팝업, Canvas, ScrollView 초기화 코드가 REPL에서 실행될 때도 실제 버튼에서 호출했을 때와 더 비슷하게 동작합니다. 즉시 평가가 필요하면 `Tools / Roslyn REPL / Run on Player Frame`에서 끌 수 있습니다.

스니펫은 Unity Editor 메인 스레드에서 실행되므로 일반 Editor API와 Unity API를 그대로 사용할 수 있습니다.

```csharp
var selected = UnityEditor.Selection.activeObject;
return selected != null ? selected.name : "Nothing selected";
```

값을 Output 트리로 보고 싶을 때는 `return`을 사용합니다. 로그만 남기는 스니펫도 가능합니다.

```csharp
Debug.Log("No return value needed");
```

### 펼쳐볼 수 있는 Output 트리

반환값은 단순한 `ToString()` 문자열이 아니라 펼쳐볼 수 있는 트리로 렌더링됩니다.

지원하는 값의 예시는 다음과 같습니다.

- primitive 값,
- 문자열과 enum,
- Unity 오브젝트,
- 일반 C# 객체,
- 필드와 안전한 사용자 정의 property,
- 배열과 리스트,
- 딕셔너리,
- 중첩된 객체 그래프,
- 파괴된 Unity 오브젝트.

각 행은 이름, 타입, 미리보기 값을 함께 보여주므로 큰 결과도 읽기 쉽습니다.

### Object Browser

Object Browser는 검색 코드를 먼저 쓰지 않아도 프로젝트 안의 살아 있는 객체를 찾게 도와줍니다.

탐색 대상:

- 씬의 `MonoBehaviour` 인스턴스,
- 로드된 `ScriptableObject` 인스턴스,
- 싱글톤처럼 보이는 객체,
- 위 항목을 합친 `All` 결과.

Output 모드에서 항목을 더블클릭하면 해당 객체를 조사하는 스니펫이 생성됩니다. 아래 패널을 Patches 모드로 바꾼 뒤 더블클릭하면 그 객체의 런타임 타입에서 패치할 메서드를 고를 수 있습니다.

브라우저는 기본 200개까지 먼저 보여주고, 검색 입력은 잠시 멈춘 뒤 다시 스캔합니다. 큰 프로젝트에서도 반응성을 유지하기 위한 동작입니다. 더 많은 결과가 있으면 카운트 옆에 **Load more**가 나타납니다.

### Snippets, History, Usings

유용한 조사는 Snippets에 저장하고 나중에 다시 불러올 수 있습니다. 스니펫과 실행 히스토리는 프로젝트별로 저장되므로 다른 Unity 프로젝트와 섞이지 않습니다.

기본 스니펫은 다음 메뉴로 가져옵니다.

```text
Tools / Roslyn REPL / Import Default Snippets
```

자주 쓰는 namespace는 **Usings**에 한 번 추가합니다. `using` 키워드는 쓰지 않습니다.

```text
MyGame.Runtime
MyGame.EditorTools
System.Text.RegularExpressions
```

REPL은 실행할 때마다 기본 using과 프로젝트별 custom using을 합쳐서 컴파일합니다.

### 이전 결과 `_`

이전 실행에서 성공적으로 반환된 non-null 값은 `_`로 사용할 수 있습니다.

```csharp
return 41;
```

그 다음:

```csharp
return _ + 1;
```

`_`는 `dynamic`으로 노출되므로 흔한 후속 표현식은 캐스팅 없이 동작합니다. 실패한 실행, 취소된 실행, 로그만 있는 스니펫, `null` 반환, Watch refresh는 `_`를 덮어쓰지 않습니다.

### Watch 패널

Watch 패널은 각 Run 이후 등록된 표현식을 다시 평가합니다.

반복해서 보고 싶은 값을 넣어두면 좋습니다.

```csharp
Time.frameCount
Selection.activeObject
GameManager.Instance.CurrentState
```

Watch 행은 단순 값뿐 아니라 펼쳐볼 수 있는 트리도 보여줄 수 있습니다. 평범한 컴파일, 이전 결과 `_`, 글로벌 오브젝트 검색 fallback을 통해 값을 찾을 수 있고, 각 행에는 값의 출처가 표시됩니다.

Watch 컴파일은 캐시됩니다. 각 행의 wrapped source는 처음 한 번 컴파일되고 이후 refresh에서는 캐시된 `MethodInfo`를 재사용합니다. assembly가 새로 로드되면 캐시는 자동으로 무효화됩니다. 반면 메인 편집기의 Run은 항상 최신 Editor 상태를 기준으로 새로 컴파일합니다.

### Runtime Method Patch

Runtime Method Patch는 Editor가 실행 중일 때 `void` instance method를 임시로 교체하는 기능입니다.

활용 예시:

- 소스 파일을 직접 수정하지 않고 로그 추가,
- Play Mode에서 작은 동작 변경 실험,
- 조사 중 특정 분기 우회,
- 실제 `.cs` 파일에 넣기 전 수정안 프로토타이핑.

다음 메뉴에서 엽니다.

```text
Tools / Roslyn REPL / Patch Method…
```

대상 타입과 메서드를 고르고, 교체할 body를 작성한 뒤 **Apply Patch**를 누릅니다.

패치 body 예시:

```csharp
Debug.Log($"Damage called: amount={amount}");

hp -= amount;

if (hp <= 0)
{
    Die();
}
```

패치 body는 원래 소스 코드처럼 작성할 수 있습니다. private field, private method, private static member, 명시적 `this`, `nameof(member)`, compound assignment, 명시적 generic method call은 직접 접근이 불가능한 경우 생성된 reflection helper로 자동 라우팅됩니다.

**Pull Original**을 누르면 현재 소스의 메서드 body를 가져와 그 자리에서 수정할 수 있습니다. **Browse**로 메서드를 고르거나 Patches 모드에서 Object Browser 항목을 더블클릭하면 원본 body가 자동으로 pull되어 바로 편집할 수 있습니다.

원본 body를 가져온 뒤 수정하면 Patches view가 원본과 현재 수정본의 live diff를 보여줍니다. **Copy diff**는 unified diff를 복사하고, **Apply to file**은 현재 patch body를 실제 대상 메서드의 `.cs` 파일에 씁니다. 쓰기 전 `.bak` 백업 파일이 같은 폴더에 생성됩니다.

패치는 개별 Revert 또는 Revert All로 되돌릴 수 있습니다. Active patch는 프로젝트별로 기억되고 가능한 경우 domain reload 이후 다시 적용됩니다. 자동 재적용은 다음 메뉴에서 끌 수 있습니다.

```text
Tools / Roslyn REPL / Auto-reapply Patches on Reload
```

자동 재적용이 꺼져 있으면 패치 목록은 남아 있지만 reload 때 detour가 설치되지 않고 dormant 상태로 표시됩니다. 설정을 다시 켜면 dormant patch가 즉시 설치되고, 행별 **Apply**로 하나씩 다시 설치할 수도 있습니다.

지원 대상:

- instance method,
- `void` 반환 타입,
- 일반 값 파라미터,
- constructor 아님,
- property accessor 아님.

패치 body에서는 원본 메서드의 parameter 이름을 그대로 사용할 수 있습니다. 명시적인 reflection 접근이 필요하면 아래 helper도 사용할 수 있습니다.

| Helper | 용도 |
|---|---|
| `__instance` | 대상 인스턴스. declaring type으로 typed 됩니다. |
| `__get<T>("name")` | 대상 인스턴스의 field 또는 property 읽기. |
| `__set("name", value)` | 대상 인스턴스의 field 또는 property 쓰기. |
| `__call<T>("name", args...)` | 대상 인스턴스의 method 호출. |
| `__getOn<T>(target, "name")` | 다른 객체에서 값 읽기. |
| `__setOn(target, "name", value)` | 다른 객체에 값 쓰기. |
| `__callOn<T>(target, "name", args...)` | 다른 객체의 method 호출. |
| `__getStatic<T>(typeof(X), "name")` | static field 또는 property 읽기. |
| `__setStatic(typeof(X), "name", value)` | static field 또는 property 쓰기. |
| `__callStatic<T>(typeof(X), "name", args...)` | static method 호출. |

대부분의 patch body는 자연스러운 소스 코드 형태가 자동으로 rewrite되므로 helper를 직접 쓸 필요가 없습니다.

## Editor 전용 빌드 영향

이 패키지는 Player 빌드 결과물에 영향을 주지 않도록 구성되어 있습니다.

- 모든 스크립트가 `Editor/` 아래에 있습니다.
- asmdef가 `Editor` 플랫폼으로 제한되어 있습니다.
- Roslyn과 Harmony plugin importer는 `Editor`에서만 켜져 있고 `Any Platform`에서는 꺼져 있습니다.
- runtime assembly, `Resources`, `StreamingAssets`, build preprocessor, postprocessor가 없습니다.

Runtime Method Patch는 현재 Editor 프로세스 안에서 Harmony detour를 거는 기능입니다. 이 detour 자체는 Player 빌드에 들어가지 않습니다. 단, **Apply to file**은 실제 `.cs` 소스 파일을 수정하는 기능이므로 그 소스 변경은 수동 코드 수정과 마찬가지로 이후 빌드에 포함됩니다.

## 데이터와 정리

아래 데이터는 Unity 프로젝트별로 로컬 저장됩니다.

- snippets,
- run history,
- custom usings,
- watch expressions,
- runtime method patch specs.

저장 위치: `<project>/UserSettings/RoslynRepl/*.json` — patches, snippets, run history, watch expressions가 Unity 프로젝트의 `UserSettings/` 폴더 안에 plain JSON으로 저장됩니다. 처음 저장될 때 패키지가 같은 폴더에 `.gitignore`(`*` 한 줄)를 자동 생성하므로, 프로젝트 상위의 `.gitignore`가 `UserSettings/`를 덮지 않더라도 이 파일들이 `git status`에 뜨지 않습니다. Custom usings와 작은 editor toggle 값들은 여전히 `EditorPrefs`에 보관됩니다.

내용은 디버그 중 임시 스크래치 버퍼처럼 다루세요. patch body나 run history에는 디버그 중 직접 입력한 서버 URL, auth token, 계정 정보 등이 그대로 들어갈 수 있습니다. 자동 생성되는 `.gitignore`가 안전장치이지만, 팀에서 `UserSettings/`를 다른 도구(예: Plastic SCM workspace settings, 별도 백업 스크립트)로 미러링한다면 editor log를 다룰 때와 같은 기준으로 그 경로도 점검해 주세요.

현재 프로젝트의 REPL 데이터를 지우려면 다음 메뉴를 사용합니다.

```text
Tools / Roslyn REPL / Reset Project Data
```

Reset은 snippets, history, watches, custom usings, 이전 결과 `_`, 보이는 Output, 저장된 runtime patch specs, in-memory compiled-watch cache를 지웁니다. Active Harmony patch도 reset 과정에서 revert됩니다.

### 메모리와 Domain Reload

Run, Watch refresh, Apply Patch는 작은 dynamic assembly를 로드합니다. 이 assembly는 script domain reload 전까지 unload되지 않습니다. 툴바에는 dynamic assembly count가 표시됩니다. 클릭하면 확인 dialog를 열 수 있고, 아래 메뉴로 직접 domain reload를 요청할 수도 있습니다.

```text
Tools / Roslyn REPL / Force Domain Reload
```

일반적인 Unity 설정에서는 스크립트 재컴파일이나 Play Mode 전환 때도 domain reload가 발생하므로 대부분 자연스럽게 정리됩니다.

## 안전 노트

이 도구는 Unity 프로젝트 안에서 Editor 코드를 실행합니다. 스니펫과 패치는 Editor 스크립트처럼 다뤄야 합니다.

- 씬 상태를 읽고 바꿀 수 있습니다.
- 프로젝트 API를 호출할 수 있습니다.
- asset을 만들 수 있습니다.
- method나 property getter를 통해 side effect를 일으킬 수 있습니다.
- 끝나지 않는 긴 루프는 Editor를 멈출 수 있습니다.

### Cooperative Cancel Only

스니펫은 Unity Editor 메인 스레드에서 동기 실행됩니다. timeout은 코드가 cancellation token `ct`를 확인할 때만 동작합니다.

```csharp
while (some_condition)
{
    ct.ThrowIfCancellationRequested();
    DoWork();
}
```

`ct`를 확인하지 않는 코드, 예를 들어 `while (true) {}`나 반환하지 않는 blocking call은 Unity Editor를 멈출 수 있고, 경우에 따라 Unity 프로세스를 강제 종료해야 합니다. 첫 Run 때 이 내용을 확인하는 dialog가 표시되고, 이후에는 Code header 아래에 경고가 계속 표시됩니다.

작은 probe 위주로 실행하고, 긴 loop에는 `ct`를 넣고, 테스트가 끝난 runtime patch는 revert하세요.

## 문제 해결

### 컴파일이 바로 실패함

`Tools / Roslyn REPL / Verify Setup`을 실행합니다.

Roslyn이 없다고 나오면 `Tools / Roslyn REPL / Install Roslyn DLLs`를 실행합니다.

### Namespace를 찾지 못함

**Usings**를 열고 `using` 키워드 없이 namespace만 추가합니다.

```text
MyGame.Runtime
```

해당 namespace를 정의한 assembly가 Editor에서 컴파일되는지도 확인하세요.

### Editor가 멈춤

스니펫이나 패치가 메인 스레드에서 blocking 작업을 하고 있을 수 있습니다. 필요하면 Editor를 멈춘 뒤, 다음 실행 전 cancellation check나 명확한 loop bound를 넣으세요.

### Watch 값의 출처가 예상과 다름

Watch는 일반 컴파일이 실패하면 이전 결과 `_` 또는 글로벌 오브젝트 검색 fallback을 사용할 수 있습니다. 행의 source label을 확인하고, 필요하면 `GameManager.State`처럼 owner를 명시하세요.

### Runtime Patch가 적용되지 않음

`Verify Setup`에서 Harmony가 present인지 확인합니다. 대상이 instance `void` method인지, parameter type list가 실제 signature와 맞는지도 확인하세요.

### Microsoft.CodeAnalysis 중복 assembly

다른 패키지가 Roslyn을 포함하고 있을 수 있습니다. `Verify Setup`으로 Roslyn assembly 위치를 확인한 뒤, Editor에서 사용할 호환 버전 하나만 활성화하세요.

## 메뉴

| Menu | Action |
|---|---|
| `Tools / Roslyn REPL / Open` | 메인 REPL 창을 엽니다. |
| `Tools / Roslyn REPL / Patch Method…` | 패치 모드로 REPL 창을 엽니다. |
| `Tools / Roslyn REPL / Import Default Snippets` | 기본 starter snippet을 추가합니다. |
| `Tools / Roslyn REPL / Verify Setup` | 컴파일러와 패치 의존성을 확인합니다. |
| `Tools / Roslyn REPL / Install Roslyn DLLs` | Roslyn과 Harmony 의존성을 설치하거나 복구합니다. |
| `Tools / Roslyn REPL / Reset Project Data` | 현재 프로젝트의 REPL 데이터를 지우고 active patch를 revert합니다. |
| `Tools / Roslyn REPL / Auto-reapply Patches on Reload` | domain reload 후 active patch specs를 다시 설치할지 토글합니다. |
| `Tools / Roslyn REPL / Run on Player Frame` | Play Mode Run을 한 프레임 뒤 Player Update에서 실행할지 토글합니다. |
| `Tools / Roslyn REPL / Force Domain Reload` | dynamic REPL assembly unload를 위해 script domain reload를 요청합니다. |

## 라이선스

이 패키지는 MIT 라이선스입니다.

설치된 Roslyn과 Harmony DLL도 각 저자의 MIT 라이선스를 따릅니다. `Editor/Plugins/Roslyn/THIRD_PARTY_NOTICES.md`와 `Editor/Plugins/Harmony/THIRD_PARTY_NOTICES.md`를 참고하세요.

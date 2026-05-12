# Unity용 Roslyn REPL

[English](README.md) | [한국어](README_kr.md)

Unity 에디터 안에서 C# 코드를 바로 실행하고, 살아 있는 오브젝트를 들여다보고, 자주 쓰는 조사 코드는 저장해 두고, 디버깅 중에 메서드를 잠깐 바꿔 끼워볼 수 있는 에디터 전용 도구입니다.

임시 에디터 스크립트를 만들거나 게임 코드 곳곳에 로그를 심기 전에, 한 창에서 빠르게 확인하고 다음 작업으로 넘어가고 싶은 개발자를 위해 만들었습니다. 스니펫을 실행하고, 결과를 펼쳐 보고, 쓸 만한 코드는 저장해 두면 됩니다.

## 설치

### 요구 사항

- Unity 2022.3 이상이 좋습니다.
- 에디터 전용 패키지라 Player 빌드에는 포함되지 않습니다.

### OpenUPM으로 설치

OpenUPM CLI가 가장 간편합니다.

```powershell
openupm add com.youngchan.roslyn-repl
```

scoped registry를 직접 추가해도 됩니다.

```text
Name:   OpenUPM
URL:    https://package.openupm.com
Scope:  com.youngchan
```

그런 다음 manifest에 이름으로 등록하세요.

```json
{
  "dependencies": {
    "com.youngchan.roslyn-repl": "0.7.3"
  }
}
```

패키지 페이지:

```text
https://openupm.com/packages/com.youngchan.roslyn-repl/
```

### Git URL로 설치

scoped registry가 부담스럽다면 Git URL로 바로 가져올 수도 있습니다.

```json
"com.youngchan.roslyn-repl": "https://github.com/djdcks12/UNITY-ROSLYN-REPL.git#v0.7.3"
```

### 로컬 디스크에서 설치

1. Unity Package Manager를 엽니다.
2. `+` 버튼을 누릅니다.
3. `Add package from disk...`를 고릅니다.
4. 이 패키지의 `package.json`을 선택합니다.

폴더째로 아래 위치에 두는 방법도 있습니다.

```text
Packages/com.youngchan.roslyn-repl/
```

## 첫 설정

Roslyn과 Harmony는 `Editor/Plugins/` 아래에 에디터 전용 플러그인으로 함께 들어 있습니다. 패키지를 가져온 다음 먼저 아래 메뉴로 상태를 확인해 주세요.

```text
Tools / Roslyn REPL / Verify Setup
```

`Verify Setup`은 컴파일러와 패치용 의존성이 제대로 잡혔는지 점검하고, 어셈블리 중복처럼 자주 발목 잡는 문제도 짚어 줍니다. 의존성이 빠졌거나 지워졌다면 다음 메뉴로 다시 설치할 수 있습니다.

```text
Tools / Roslyn REPL / Install Roslyn DLLs
```

## 어떤 일을 도와주나요

- 에디터 안에서 C# 스니펫을 그 자리에서 실행
- 씬 오브젝트, ScriptableObject, 딕셔너리, 리스트, 사용자 정의 클래스를 펼쳐 보는 트리 뷰
- 살아 있는 씬 오브젝트, 로드된 에셋, 흔한 싱글턴 패턴을 브라우저로 탐색
- 프로젝트별로 스니펫과 실행 히스토리 보관
- 자주 쓰는 namespace를 `using` 목록에 한 번 등록해 두고 짧게 작성
- 직전 실행 결과를 `_`로 이어쓰기
- 매 실행 후 자동으로 다시 평가되는 Watch 식
- Harmony 기반 Runtime Method Patch로 메서드 동작을 잠깐 바꿔치기
- Player 빌드에는 어떤 의존성도 남기지 않고 에디터 안에서만 동작

## 빠른 시작

1. `Tools / Roslyn REPL / Open`을 엽니다.
2. 스니펫을 입력합니다.
3. **Run**, `F5`, 또는 `Ctrl+Enter`를 누릅니다.
4. Output에서 로그와 반환값을 확인합니다.
5. 다시 쓸 만한 조사는 **Snippets**에 저장합니다.
6. 계속 들여다볼 값은 **Watch**에 추가합니다.

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

메인 창에서 멀티라인 C# 편집기와 함께 라인 번호, 커서 위치, 단축키, 컴파일 진단, 런타임 예외, 캡처된 로그, 실행 시간이 한눈에 보입니다.

Play Mode에서는 스니펫이 짧은 코루틴을 거쳐 한 프레임 뒤에 실행됩니다. 호출 시점이 평소의 `Button.onClick`과 같은 Player Update 구간에 맞춰지기 때문에, UI나 팝업, Canvas, ScrollView 초기화처럼 프레임 흐름에 민감한 코드도 실제 버튼에서 호출했을 때와 거의 똑같이 동작합니다. 즉시 평가가 필요하면 `Tools / Roslyn REPL / Run on Player Frame`에서 끄면 됩니다.

스니펫은 Unity 에디터 메인 스레드에서 돌기 때문에, 일반적인 에디터 API와 Unity API를 그대로 부를 수 있습니다.

```csharp
var selected = UnityEditor.Selection.activeObject;
return selected != null ? selected.name : "Nothing selected";
```

값을 Output 트리로 보고 싶다면 `return`을 쓰세요. 로그만 남기는 스니펫도 그대로 동작합니다.

```csharp
Debug.Log("No return value needed");
```

### 펼쳐 보는 Output 트리

반환값은 평범한 `ToString()` 문자열이 아니라 가지마다 펼쳐 볼 수 있는 트리로 그려집니다.

다음과 같은 값을 다룹니다.

- 기본형(primitive)
- 문자열, enum
- Unity 오브젝트
- 평범한 C# 객체
- 인스턴스/private/상속받은 필드
- 배열, 리스트
- 딕셔너리
- 중첩된 객체 그래프
- 파괴된 Unity 오브젝트

각 행에 이름, 타입, 미리보기 값이 함께 나오기 때문에, 결과가 커도 한 번에 훑어보기 좋습니다.

기본적으로는 필드만 따라갑니다. property getter는 lazy init이나 IO, 로그 출력, 상태 변경 같은 사용자 코드를 돌릴 수 있어서, 값을 들여다보는 행위가 프로젝트 상태를 몰래 바꾸는 일은 막아두는 게 안전합니다. property까지 트리에 보고 싶다면 `Tools / Roslyn REPL / Output: Include Property Getters`를 켜고 다시 Run 하세요. Watch 트리는 어떤 경우에도 필드만 보며, 이 토글에 영향받지 않습니다.

### Object Browser

Object Browser는 검색 코드를 따로 쓰지 않아도 프로젝트 안의 살아 있는 객체를 바로 찾을 수 있게 해 줍니다.

탐색 대상:

- 씬에 올라온 `MonoBehaviour` 인스턴스
- 로드된 `ScriptableObject` 인스턴스
- 싱글턴처럼 보이는 객체
- 위 셋을 묶은 `All` 결과

Output 모드에서 항목을 더블클릭하면 그 객체를 들여다보는 스니펫이 자동으로 만들어집니다. 아래 패널을 Patches 모드로 바꾼 뒤 더블클릭하면, 그 객체의 런타임 타입에서 패치할 메서드를 골라 작업을 시작할 수 있습니다.

브라우저는 기본 200개까지 먼저 보여주고 검색 입력은 살짝 디바운스를 둔 뒤에 다시 훑습니다. 큰 프로젝트에서도 입력 반응이 끊기지 않게 하려는 장치입니다. 결과가 더 있으면 카운트 옆에 **Load more**가 나타납니다.

### Snippets, History, Usings

쓸 만한 조사는 Snippets에 저장해 두고 언제든 다시 꺼내 쓸 수 있습니다. 스니펫과 실행 히스토리는 프로젝트별로 따로 보관되니, 다른 Unity 프로젝트의 디버깅 코드와 섞이는 일은 없습니다.

기본 스니펫은 다음 메뉴로 가져옵니다.

```text
Tools / Roslyn REPL / Import Default Snippets
```

자주 쓰는 namespace는 **Usings**에 한 번만 등록해 두면 됩니다. `using` 키워드는 빼고 namespace 이름만 적습니다.

```text
MyGame.Runtime
MyGame.EditorTools
System.Text.RegularExpressions
```

REPL은 매번 실행할 때마다 기본 using과 여기 등록한 custom using을 합쳐서 컴파일에 사용합니다.

### 직전 결과 `_`

성공적으로 반환된 non-null 값은 다음 실행에서 `_`로 바로 받아 쓸 수 있습니다.

```csharp
return 41;
```

이어서:

```csharp
return _ + 1;
```

`_`는 `dynamic`으로 노출되므로 흔한 후속 표현은 캐스팅 없이 그대로 쓸 수 있습니다. 실패한 실행, 취소된 실행, 로그만 남긴 스니펫, `null` 반환, Watch refresh는 `_`를 건드리지 않습니다.

### Watch 패널

Watch 패널은 매 Run 직후 등록해 둔 표현식을 한 번씩 다시 평가해 줍니다.

같은 값을 반복해서 들여다봐야 할 때 쓰기 좋습니다.

```csharp
Time.frameCount
Selection.activeObject
GameManager.Instance.CurrentState
```

각 행은 단순 값뿐 아니라 펼쳐 볼 수 있는 트리도 보여줄 수 있습니다. 평소대로 컴파일해 평가하거나, 직전 결과 `_`, 또는 글로벌 오브젝트 검색 fallback을 통해서도 값을 풀어내며, 어떤 경로로 잡힌 값인지 행마다 출처가 표시됩니다.

Watch 컴파일은 캐시를 둡니다. 각 행의 wrapped source는 처음 한 번만 컴파일하고, 이후 refresh에서는 캐시된 `MethodInfo`를 다시 씁니다. 어셈블리가 새로 로드되면 캐시는 자동으로 무효가 되고, 메인 편집기의 Run은 항상 최신 에디터 상태를 기준으로 매번 새로 컴파일합니다.

### Runtime Method Patch

Runtime Method Patch는 에디터가 실행 중인 동안 `void` 인스턴스 메서드의 동작을 잠깐 바꿔 끼우는 기능입니다.

이런 상황에서 도움이 됩니다.

- 소스 파일을 건드리지 않고 잠깐 로그만 추가해 보고 싶을 때
- Play Mode에서 동작을 살짝 바꿔서 실험해 보고 싶을 때
- 조사 중에 특정 분기를 건너뛰어야 할 때
- 실제 `.cs`에 반영하기 전에 수정안을 먼저 끼워 보고 싶을 때

다음 메뉴에서 엽니다.

```text
Tools / Roslyn REPL / Patch Method…
```

대상 타입과 메서드를 고르고, 새 body를 작성한 뒤 **Apply Patch**를 누르면 됩니다.

패치 body 예시:

```csharp
Debug.Log($"Damage called: amount={amount}");

hp -= amount;

if (hp <= 0)
{
    Die();
}
```

패치 body는 원래 소스를 쓰듯 자연스럽게 작성하면 됩니다. private field나 method, private static 멤버, 명시적 `this`, `nameof(member)`, compound 대입, 명시적 제네릭 메서드 호출 등 직접 접근이 막혀 있는 표현은 생성되는 reflection helper로 자동 라우팅됩니다.

**Pull Original**을 누르면 현재 소스의 메서드 body를 그대로 끌어와 그 자리에서 수정할 수 있습니다. **Browse**로 메서드를 고르거나 Patches 모드에서 Object Browser 항목을 더블클릭하면, 원본 body가 자동으로 채워져 바로 편집 상태로 이어집니다.

원본을 가져온 뒤 손을 대면 Patches 뷰가 원본과 현재 수정본의 차이를 실시간으로 보여 줍니다. **Copy diff**는 unified diff를 클립보드로 복사하고, **Apply to file**은 현재 patch body를 실제 대상 메서드의 `.cs`에 써 줍니다. 쓰기 직전에 타임스탬프가 붙은 백업이 `<project>/Library/RoslynRepl/Backups/` 아래에 자동으로 저장되니, 필요하면 손으로 되돌릴 수 있습니다. 이 폴더는 Unity가 무시하므로 Project 창에 잡히거나 실수로 커밋되는 일이 없고, Library 리임포트 시에는 비워질 수 있으니 오래 보관해야 한다면 따로 옮겨 두세요.

패치는 행별 Revert나 Revert All로 되돌릴 수 있습니다. **Revert**는 행을 Inactive draft로 남겨 둬서 나중에 `Load`로 다시 불러와 쓸 수 있고, **Delete**는 draft까지 통째로 목록에서 지웁니다(Active 상태였다면 Harmony detour를 먼저 자동으로 revert합니다). draft가 더 이상 의미가 없을 때 한 행만 정리하는 용도이며, 모든 데이터를 한 번에 비우려면 Reset Project Data를 쓰면 됩니다. Active 패치는 프로젝트별로 기억되었다가 가능한 경우 domain reload 후에 다시 적용됩니다. 자동 재적용을 끄고 싶다면 다음 메뉴를 사용하세요.

```text
Tools / Roslyn REPL / Auto-reapply Patches on Reload
```

자동 재적용을 꺼 두면 패치 목록은 그대로 남되 reload 때 detour가 다시 깔리지 않고 dormant 상태로만 표시됩니다. 다시 켜면 dormant 패치가 곧바로 설치되고, 행마다 **Apply**로 하나씩 다시 깔 수도 있습니다.

지원 대상:

- 인스턴스 메서드
- `void` 반환 타입
- 일반 값 파라미터
- 생성자 제외
- property accessor 제외

패치 body 안에서는 원본 메서드의 파라미터 이름을 그대로 쓸 수 있습니다. 명시적인 reflection 접근이 필요하면 다음 helper도 마련돼 있습니다.

| Helper | 용도 |
|---|---|
| `__instance` | 대상 인스턴스. declaring type으로 typed 됩니다. |
| `__get<T>("name")` | 대상 인스턴스의 field 또는 property 읽기 |
| `__set("name", value)` | 대상 인스턴스의 field 또는 property 쓰기 |
| `__call<T>("name", args...)` | 대상 인스턴스의 method 호출 |
| `__getOn<T>(target, "name")` | 다른 객체에서 값 읽기 |
| `__setOn(target, "name", value)` | 다른 객체에 값 쓰기 |
| `__callOn<T>(target, "name", args...)` | 다른 객체의 method 호출 |
| `__getStatic<T>(typeof(X), "name")` | static field 또는 property 읽기 |
| `__setStatic(typeof(X), "name", value)` | static field 또는 property 쓰기 |
| `__callStatic<T>(typeof(X), "name", args...)` | static method 호출 |

대부분의 patch body는 자연스러운 소스 형태가 그대로 자동 rewrite되므로, helper를 직접 호출할 일은 거의 없습니다.

## 에디터 전용 / 빌드 영향

Player 빌드 결과물에 어떤 영향도 주지 않도록 만들었습니다.

- 모든 스크립트가 `Editor/` 아래에 모여 있고,
- asmdef도 `Editor` 플랫폼으로만 묶여 있고,
- Roslyn과 Harmony plugin importer는 `Editor`에서만 켜져 있고 `Any Platform`에서는 꺼져 있고,
- runtime 어셈블리, `Resources`, `StreamingAssets`, build preprocessor / postprocessor가 전부 없습니다.

Runtime Method Patch는 에디터 프로세스 안에서만 Harmony detour를 거는 기능이라, detour 자체가 Player 빌드에 들어가는 일은 없습니다. 다만 **Apply to file**은 실제 `.cs` 소스를 직접 고치는 동작이므로, 그 변경 자체는 손으로 코드를 수정한 것과 똑같이 이후 빌드에 반영됩니다.

## 데이터와 정리

다음 항목이 Unity 프로젝트별로 로컬 저장됩니다.

- snippets
- run history
- custom usings
- watch expressions
- runtime method patch specs

저장 위치는 `<project>/UserSettings/RoslynRepl/*.json`입니다. patches, snippets, run history, watch expressions가 Unity 프로젝트의 `UserSettings/` 폴더 안에 평문 JSON으로 들어갑니다. 패키지가 처음 저장할 때 같은 폴더에 `.gitignore`(`*` 한 줄)를 자동으로 만들어 주기 때문에, 프로젝트 상위 `.gitignore`가 `UserSettings/`를 덮지 않더라도 이 파일들이 `git status`에 잡히는 일은 없습니다. custom usings와 작은 에디터 토글 값은 여전히 `EditorPrefs`에 남습니다.

저장된 내용은 디버깅 중 잠깐 쓰는 스크래치 버퍼라고 생각해 주세요. patch body나 run history에는 디버그 도중에 직접 입력한 서버 URL, auth token, 계정 값 같은 것들이 그대로 들어 있을 수 있습니다. 자동 생성되는 `.gitignore`가 1차 방어선이지만, 팀에서 `UserSettings/`를 다른 도구(예: Plastic SCM workspace settings, 별도 백업 스크립트 등)로 미러링하고 있다면 에디터 로그를 다룰 때와 같은 기준으로 그쪽 경로도 한 번씩 점검해 두면 좋습니다.

현재 프로젝트의 REPL 데이터를 한꺼번에 비우려면 다음 메뉴를 사용하세요.

```text
Tools / Roslyn REPL / Reset Project Data
```

Reset은 snippets, history, watches, custom usings, 직전 결과 `_`, 화면에 남은 Output, 저장된 runtime patch specs, in-memory compiled-watch 캐시까지 모두 비우고, 활성화돼 있던 Harmony 패치도 함께 revert합니다.

### 메모리와 Domain Reload

Run, Watch refresh, Apply Patch는 그때마다 작은 dynamic 어셈블리를 하나씩 로드합니다. 이 어셈블리는 script domain reload가 일어나기 전까지 메모리에서 빠지지 않습니다. 툴바에는 현재 로드된 dynamic 어셈블리 개수가 표시되며, 클릭하면 확인 dialog가 열립니다. 직접 reload를 요청하고 싶다면 아래 메뉴를 쓰면 됩니다.

```text
Tools / Roslyn REPL / Force Domain Reload
```

평범한 Unity 사용 환경에서는 스크립트 재컴파일이나 Play Mode 진입/종료 시점에 자연스럽게 domain reload가 일어나기 때문에, 보통은 별도로 신경 쓰지 않아도 누적된 어셈블리가 정리됩니다.

## 안전 노트

이 도구는 Unity 프로젝트 안에서 에디터 코드를 그대로 실행합니다. 즉 스니펫과 패치는 평범한 에디터 스크립트와 같은 권한을 갖는다고 봐야 합니다.

- 씬 상태를 읽고 바꿀 수 있고,
- 프로젝트 API를 호출할 수 있고,
- 에셋을 만들 수 있고,
- 메서드나 property getter로 부수효과를 일으킬 수 있고,
- 끝나지 않는 긴 루프는 에디터 자체를 멈출 수 있습니다.

### 협력형 취소만 동작 (Cooperative Cancel)

스니펫은 Unity 에디터 메인 스레드에서 동기로 실행됩니다. 따라서 timeout은 코드 쪽이 cancellation token `ct`를 들여다볼 때만 효력이 있습니다.

```csharp
while (some_condition)
{
    ct.ThrowIfCancellationRequested();
    DoWork();
}
```

`ct`를 확인하지 않는 코드, 예를 들어 `while (true) {}`나 빠져나오지 않는 blocking 호출은 에디터를 그대로 멈추게 만들 수 있고, 심한 경우 Unity 프로세스를 강제 종료해야 할 수도 있습니다. 첫 Run 때 이 내용을 안내하는 dialog가 한 번 뜨고, 이후로는 Code 헤더 아래에 경고 표시가 계속 남아 있습니다.

가능하면 짧은 probe 위주로 돌리고, 길어질 만한 루프에는 `ct` 체크를 끼워 두고, 테스트가 끝난 runtime 패치는 잊지 말고 revert해 주세요.

## 문제 해결

### 컴파일이 곧바로 실패할 때

`Tools / Roslyn REPL / Verify Setup`을 한 번 돌려 보세요.

Roslyn이 없다고 나오면 `Tools / Roslyn REPL / Install Roslyn DLLs`로 다시 설치할 수 있습니다.

### Namespace를 못 찾는다고 할 때

**Usings**를 열고 `using` 키워드 없이 namespace 이름만 추가해 보세요.

```text
MyGame.Runtime
```

해당 namespace를 정의한 어셈블리가 에디터에서 컴파일되고 있는지도 함께 확인해 주세요.

### 에디터가 멈춘 것 같을 때

스니펫이나 패치가 메인 스레드에서 blocking 작업을 하고 있을 가능성이 큽니다. 일단 에디터를 정지시킨 뒤, 다음 실행 전에 cancellation 체크를 넣거나 루프에 명확한 종료 조건을 더해 주세요.

### Watch 값이 예상한 곳에서 오지 않을 때

Watch는 평범한 컴파일이 실패하면 직전 결과 `_`나 글로벌 오브젝트 검색 fallback을 써서 값을 잡으려 합니다. 행에 표시되는 source label을 확인해 보고, 필요하면 `GameManager.State`처럼 owner를 명시해 주세요.

### Runtime 패치가 적용되지 않을 때

`Verify Setup`에서 Harmony가 present로 나오는지 확인합니다. 그리고 대상이 인스턴스 `void` 메서드인지, 입력한 parameter type 목록이 실제 시그니처와 정확히 맞는지도 함께 점검하세요.

### Microsoft.CodeAnalysis 어셈블리 중복

다른 패키지가 자체 Roslyn을 끌고 들어왔을 가능성이 있습니다. `Verify Setup`으로 어떤 위치의 Roslyn이 잡히는지 확인한 뒤, 에디터에서 사용할 호환 버전 하나만 활성화해 두세요.

## 메뉴

| Menu | Action |
|---|---|
| `Tools / Roslyn REPL / Open` | 메인 REPL 창을 엽니다. |
| `Tools / Roslyn REPL / Patch Method…` | 패치 모드로 REPL 창을 엽니다. |
| `Tools / Roslyn REPL / Import Default Snippets` | 기본 starter 스니펫을 가져옵니다. |
| `Tools / Roslyn REPL / Verify Setup` | 컴파일러와 패치 의존성 상태를 점검합니다. |
| `Tools / Roslyn REPL / Install Roslyn DLLs` | Roslyn과 Harmony 의존성을 설치하거나 복구합니다. |
| `Tools / Roslyn REPL / Reset Project Data` | 현재 프로젝트의 REPL 데이터를 비우고 active 패치를 revert합니다. |
| `Tools / Roslyn REPL / Auto-reapply Patches on Reload` | domain reload 후 active 패치를 자동으로 다시 설치할지 토글합니다. |
| `Tools / Roslyn REPL / Run on Player Frame` | Play Mode Run을 한 프레임 뒤 Player Update에서 실행할지 토글합니다. |
| `Tools / Roslyn REPL / Force Domain Reload` | 누적된 dynamic REPL 어셈블리를 비우려 script domain reload를 요청합니다. |

## 라이선스

이 패키지는 MIT 라이선스로 제공됩니다.

함께 들어 있는 Roslyn과 Harmony DLL도 각 저자의 MIT 라이선스를 따릅니다. 자세한 내용은 `Editor/Plugins/Roslyn/THIRD_PARTY_NOTICES.md`와 `Editor/Plugins/Harmony/THIRD_PARTY_NOTICES.md`를 참고해 주세요.

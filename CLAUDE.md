# Proj_ZAK_MapProto

대한민국 실제 지도 데이터 기반 좀비 아포칼립스 게임의 프로토타입. 1인 개발(Keb), Unity + AI 어시스트 워크플로우.

## 문서
- 기획 전체: `docs/design.md` — 작업 전 반드시 읽을 것
- 마일스톤과 현재 진행 상태: `docs/milestones.md`
- 개발 환경/도구 가이드: `docs/dev-setup.md`

## 기술 스택
- Unity 3D, Built-in Render Pipeline (URP 아님 — 셰이더 선택 시 주의)
- 좌표계: WGS84 경위도 → `GeoUtil`로 로컬 미터 변환. 앵커는 부산시청 (129.0756, 35.1796). 모든 월드 좌표는 미터 단위.
- 데이터: `Assets/StreamingAssets/`의 CSV/GeoJSON. 원본 데이터 파일은 git에 커밋하지 않음 (재다운로드 가능).

## 코드 규칙
- 스크립트는 `Assets/Scripts/`
- 카메라는 원근(perspective) 수직 탑뷰 — 건물 extrude 시 가장자리 기울어짐이 자연스럽게 나오는 구조이므로 orthographic으로 바꾸지 말 것
- 대량 오브젝트(상가 점, 건물)는 개별 GameObject 금지 — 청크 단위 메시 결합
- 뷰 폭(m)이 줌의 단일 기준값. 줌 6단계 정의는 docs/design.md 참고

## 작업 방식
- 마일스톤(M0~M6) 단위로 진행. 현재 마일스톤 범위를 벗어나는 구현은 제안만 하고 만들지 말 것
- 큰 변경 전에 계획을 먼저 설명하고 승인받을 것
- 작업 단위가 끝나면 커밋 (메시지는 한국어 가능)

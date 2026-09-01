#!/usr/bin/env bash
set -euo pipefail

repo_root="$(git rev-parse --show-toplevel)"
editor_path="${UNITY_EDITOR_PATH:-/Applications/Unity/Hub/Editor/6000.3.9f1/Unity.app/Contents/MacOS/Unity}"
sdk_image='mcr.microsoft.com/dotnet/sdk:10.0.202-noble@sha256:adc02be8b87957d07208a4a3e51775935b33bad3317de8c45b1e67357b4c073b'
runtime_image='mcr.microsoft.com/dotnet/aspnet:10.0.4-noble@sha256:8b75cdf59a5068d9adfd8a6d202cc7671b2dc8f5f46c51e3b88a0a632e8fad1f'
expected_fantasy='f8bed0d464924f159d46498f1311206ea0694be8'
expected_unity_version='6000.3.9f1'
expected_unity_revision='7a9955a4f2fa'
expected_fantasy_url='https://github.com/rayss1/Fantasy.git?path=/Fantasy.Packages/Fantasy.Unity#f8bed0d464924f159d46498f1311206ea0694be8'
kcp_port=22000
health_port=22080
container_id=''
container_name=''
player_pid=''
host_log=''

fail() {
  echo "$1" >&2
  exit 1
}

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "$1 is required for the macOS WS-26 validation." >&2
    exit 2
  fi
}

sha256() {
  shasum -a 256 "$1" | awk '{print $1}'
}

assert_nunit_result() {
  local path="$1"
  local expected_passed="$2"
  local label="$3"
  if [[ ! -s "$path" ]]; then
    fail "Unity did not produce $label NUnit results at: $path"
  fi

  local result passed failed skipped
  result="$(xmllint --xpath 'string(/test-run/@result)' "$path")"
  passed="$(xmllint --xpath 'string(/test-run/@passed)' "$path")"
  failed="$(xmllint --xpath 'string(/test-run/@failed)' "$path")"
  skipped="$(xmllint --xpath 'string(/test-run/@skipped)' "$path")"
  if [[ "$result" != 'Passed' || "$passed" != "$expected_passed" || "$failed" != '0' || "$skipped" != '0' ]]; then
    fail "Expected exactly $expected_passed passed, 0 failed, and 0 skipped $label tests; got result=$result passed=$passed failed=$failed skipped=$skipped."
  fi
}

cleanup() {
  local exit_code=$?
  trap - EXIT INT TERM

  if [[ -n "$player_pid" ]] && kill -0 "$player_pid" >/dev/null 2>&1; then
    kill -TERM "$player_pid" >/dev/null 2>&1 || true
    sleep 1
    if kill -0 "$player_pid" >/dev/null 2>&1; then
      kill -KILL "$player_pid" >/dev/null 2>&1 || true
    fi
    wait "$player_pid" >/dev/null 2>&1 || true
  fi

  local cleanup_container="${container_id:-$container_name}"
  if [[ -n "$cleanup_container" ]] && docker inspect "$cleanup_container" >/dev/null 2>&1; then
    docker stop --time 5 "$cleanup_container" >/dev/null 2>&1 || docker kill "$cleanup_container" >/dev/null 2>&1 || true
    if [[ -n "$host_log" ]]; then
      docker logs "$cleanup_container" >"$host_log" 2>&1 || true
    fi
    docker rm "$cleanup_container" >/dev/null 2>&1 || true
  fi

  exit "$exit_code"
}
trap cleanup EXIT INT TERM

for command_name in git docker colima curl jq xmllint shasum lipo plutil sw_vers uname awk grep cmp cp find seq; do
  require_command "$command_name"
done

if [[ "$(uname -s)" != 'Darwin' || "$(uname -m)" != 'arm64' ]]; then
  echo 'WS-26 requires a native Apple Silicon macOS host.' >&2
  exit 2
fi

if [[ ! -x "$editor_path" ]]; then
  echo "Unity Editor was not found at: $editor_path" >&2
  echo "Set UNITY_EDITOR_PATH to the Unity $expected_unity_version executable." >&2
  exit 2
fi

mac_support="$editor_path"
mac_support="${mac_support%/Contents/MacOS/Unity}/Contents/PlaybackEngines/MacStandaloneSupport"
if [[ ! -d "$mac_support" ]]; then
  echo 'Unity Mac Build Support (Mono) is required.' >&2
  exit 2
fi

if [[ -n "$(git -C "$repo_root" status --porcelain --untracked-files=all)" ]]; then
  echo 'The worktree is dirty. Use a clean exact-commit worktree so the evidence is attributable.' >&2
  exit 2
fi

commit="$(git -C "$repo_root" rev-parse HEAD)"
tree_identity="$(git -C "$repo_root" rev-parse HEAD^{tree})"
fantasy_commit="$(git -C "$repo_root" rev-parse HEAD:server/vendor/Fantasy)"
if [[ "$fantasy_commit" != "$expected_fantasy" ]]; then
  fail "Expected Fantasy $expected_fantasy, found: $fantasy_commit"
fi
submodule_status="$(git -C "$repo_root" submodule status --recursive)"
if grep -Eq '^[-+U]' <<<"$submodule_status"; then
  fail 'A submodule is uninitialized, conflicted, or differs from its gitlink.'
fi

editor_version="$("$editor_path" -version 2>/dev/null | tail -n 1 | tr -d '\r')"
if [[ "$editor_version" != "$expected_unity_version" ]]; then
  echo "Expected Unity $expected_unity_version, found: $editor_version" >&2
  exit 2
fi
editor_app="${editor_path%/Contents/MacOS/Unity}"
editor_revision="$(plutil -extract UnityBuildNumber raw "$editor_app/Contents/Info.plist")"
if [[ "$editor_revision" != "$expected_unity_revision" ]]; then
  echo "Expected Unity revision $expected_unity_revision, found: $editor_revision" >&2
  exit 2
fi

project_version="$repo_root/client/UnityProject/ProjectSettings/ProjectVersion.txt"
grep -Eq '^m_EditorVersion: 6000\.3\.9f1$' "$project_version" || fail 'The Unity project version is not pinned to 6000.3.9f1.'
grep -Eq '^m_EditorVersionWithRevision: 6000\.3\.9f1 \(7a9955a4f2fa\)$' "$project_version" || fail 'The Unity project revision is not pinned to 7a9955a4f2fa.'

manifest="$repo_root/client/UnityProject/Packages/manifest.json"
lock_file="$repo_root/client/UnityProject/Packages/packages-lock.json"
fantasy_package="$repo_root/packages/com.ainative.client.fantasy/package.json"
fantasy_notice="$repo_root/packages/com.ainative.client.fantasy/THIRD-PARTY-NOTICES.md"
fantasy_license="$repo_root/server/vendor/Fantasy/Fantasy.Packages/Fantasy.Unity/LICENSE"
host_config="$repo_root/server/src/Hosts/AiNative.BattleHost/Fantasy.config"
protocol_schema="$repo_root/shared/schemas/ainative/v1/gameplay.proto"

[[ "$(jq -r '.dependencies["com.fantasy.unity"]' "$manifest")" == "$expected_fantasy_url" ]] || fail 'The UPM manifest does not pin the approved Fantasy.Unity source.'
[[ "$(jq -r '.dependencies["com.fantasy.unity"].version' "$lock_file")" == "$expected_fantasy_url" ]] || fail 'The UPM lock does not retain the approved Fantasy.Unity URL.'
[[ "$(jq -r '.dependencies["com.fantasy.unity"].source' "$lock_file")" == 'git' ]] || fail 'Fantasy.Unity must remain a Git-pinned UPM dependency.'
[[ "$(jq -r '.dependencies["com.fantasy.unity"].hash' "$lock_file")" == "$fantasy_commit" ]] || fail 'The UPM lock Fantasy.Unity hash differs from the gitlink.'
[[ "$(jq -r '.dependencies["com.fantasy.unity"]' "$fantasy_package")" == '2026.1.1001' ]] || fail 'The client package Fantasy.Unity version differs from the approved version.'
grep -qi 'MIT License' "$fantasy_license" || fail 'The Fantasy license is missing its MIT heading.'
grep -qi 'explicitly prohibited' "$fantasy_license" || fail 'The Fantasy entity restriction is missing.'
grep -qi 'macOS' "$fantasy_notice" || fail 'The Fantasy notice must record the approved macOS distribution scope.'

if ! docker info >/dev/null 2>&1; then
  echo 'A running Docker/Colima engine is required.' >&2
  exit 2
fi
if [[ "$(docker context show)" != 'colima' ]] || ! colima status >/dev/null 2>&1; then
  echo 'The active Docker context must be a running Colima instance.' >&2
  exit 2
fi
docker_server_os="$(docker info --format '{{.OSType}}' 2>/dev/null)"
if [[ "$docker_server_os" != 'linux' ]]; then
  echo "Expected a Linux Docker engine, found: $docker_server_os" >&2
  exit 2
fi

evidence_dir="${1:-$repo_root/artifacts/unity-macos/$commit}"
evidence_dir="$(mkdir -p "$evidence_dir" && cd "$evidence_dir" && pwd)"
metadata="$evidence_dir/metadata.txt"
summary="$evidence_dir/summary.txt"
editmode_xml="$evidence_dir/editmode.xml"
editmode_log="$evidence_dir/editmode.log"
playmode_xml="$evidence_dir/playmode.xml"
playmode_log="$evidence_dir/playmode.log"
host_build_log="$evidence_dir/battle-host-build.log"
host_log="$evidence_dir/battle-host.log"
player_build_log="$evidence_dir/player-build.log"
player_stdout="$evidence_dir/player.stdout.log"
player_stderr="$evidence_dir/player.stderr.log"
smoke_json="$evidence_dir/smoke.json"
staged_host_config="$evidence_dir/Fantasy.config"
player_directory="$evidence_dir/player"
player_bundle="$player_directory/AiNative.BattleClient.app"
player_info_plist="$player_bundle/Contents/Info.plist"
player_executable=''
image_tag="ainative/battle-host:ws26-macos-${commit}"
protocol_identity="$(sha256 "$protocol_schema")"
configuration_identity="$(sha256 "$host_config")"
cp "$host_config" "$staged_host_config"
[[ "$(sha256 "$staged_host_config")" == "$configuration_identity" ]] || fail 'The staged Battle Host configuration identity changed.'

{
  echo "commit=$commit"
  echo "tree_identity=$tree_identity"
  echo "fantasy_commit=$fantasy_commit"
  echo "protocol_identity=$protocol_identity"
  echo "configuration_identity=$configuration_identity"
  echo "validated_at_utc=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo "host=$(uname -a)"
  echo "macos_version=$(sw_vers -productVersion)"
  echo "host_architecture=$(uname -m)"
  echo "editor_version=$editor_version"
  echo "editor_revision=$editor_revision"
  echo "editor_executable_sha256=$(sha256 "$editor_path")"
  echo "sdk_image=$sdk_image"
  echo "runtime_image=$runtime_image"
  echo "manifest_sha256=$(sha256 "$manifest")"
  echo "packages_lock_sha256=$(sha256 "$lock_file")"
  echo "fantasy_client_package_sha256=$(sha256 "$fantasy_package")"
  echo "fantasy_client_notice_sha256=$(sha256 "$fantasy_notice")"
  echo "fantasy_unity_license_sha256=$(sha256 "$fantasy_license")"
  echo "health_endpoint=http://127.0.0.1:$health_port/health/ready"
  echo "colima_status=$(colima status --json)"
  echo "$submodule_status"
} >"$metadata"

echo 'Checking Linux x64 emulation and fixed runtime image...'
docker run --rm --platform linux/amd64 --entrypoint /bin/true "$runtime_image" >/dev/null

echo 'Building the exact-source Battle Host with fixed .NET 10 images...'
if ! docker build \
  --platform linux/amd64 \
  --file "$repo_root/infrastructure/battle-host/Dockerfile" \
  --build-arg "SDK_IMAGE=$sdk_image" \
  --build-arg "RUNTIME_IMAGE=$runtime_image" \
  --build-arg "SOURCE_COMMIT=$commit" \
  --build-arg "FANTASY_COMMIT=$fantasy_commit" \
  --build-arg "PROTOCOL_IDENTITY=$protocol_identity" \
  --tag "$image_tag" \
  "$repo_root" >"$host_build_log" 2>&1; then
  tail -n 100 "$host_build_log" >&2
  fail 'The fixed-image Battle Host build failed.'
fi
image_id="$(docker image inspect "$image_tag" --format '{{.Id}}')"
image_source="$(docker image inspect "$image_tag" --format '{{index .Config.Labels "org.opencontainers.image.revision"}}')"
image_fantasy="$(docker image inspect "$image_tag" --format '{{index .Config.Labels "org.ainative.fantasy.revision"}}')"
image_protocol="$(docker image inspect "$image_tag" --format '{{index .Config.Labels "org.ainative.protocol.identity"}}')"
[[ "$image_source" == "$commit" && "$image_fantasy" == "$fantasy_commit" && "$image_protocol" == "$protocol_identity" ]] || fail 'The Battle Host image labels do not match the exact source identities.'
{
  echo "battle_host_image=$image_tag"
  echo "battle_host_image_id=$image_id"
  echo "docker_server=$(docker version --format '{{.Server.Os}}/{{.Server.Arch}} {{.Server.Version}}')"
} >>"$metadata"

echo 'Starting the exact Battle Host container...'
container_name="ainative-ws26-macos-${commit:0:12}-$$"
container_id="$container_name"
docker run -d \
  --name "$container_name" \
  --platform linux/amd64 \
  --read-only \
  --tmpfs /tmp:size=64m,mode=1777 \
  --cap-drop ALL \
  --security-opt no-new-privileges:true \
  --volume "$staged_host_config:/app/Fantasy.config:ro" \
  --publish "$health_port:8080/tcp" \
  --publish "$kcp_port:22000/udp" \
  --env "AINATIVE_SOURCE_COMMIT=$commit" \
  --env "AINATIVE_FANTASY_COMMIT=$fantasy_commit" \
  --env "AINATIVE_PROTOCOL_IDENTITY=$protocol_identity" \
  --env 'AINATIVE_FANTASY_ENABLED=true' \
  --env 'AINATIVE_FANTASY_OUTER_KCP_MTU=1150' \
  "$image_tag" >/dev/null
container_id="$(docker inspect "$container_name" --format '{{.Id}}')"

ready='false'
for _ in $(seq 1 360); do
  if [[ "$(docker inspect "$container_id" --format '{{.State.Running}}')" != 'true' ]]; then
    docker logs "$container_id" >&2 || true
    fail 'The Battle Host exited before readiness.'
  fi
  if response="$(curl --silent --show-error --max-time 2 "http://127.0.0.1:$health_port/health/ready" 2>/dev/null)" &&
     [[ "$(jq -r '.status // empty' <<<"$response")" == 'ready' ]]; then
    ready='true'
    break
  fi
  sleep 0.25
done
[[ "$ready" == 'true' ]] || fail 'The Battle Host did not become ready within 90 seconds.'

kcp_host=''
while IFS= read -r candidate; do
  [[ -n "$candidate" ]] || continue
  if response="$(curl --silent --show-error --max-time 2 "http://$candidate:$health_port/health/ready" 2>/dev/null)" &&
     [[ "$(jq -r '.status // empty' <<<"$response")" == 'ready' ]]; then
    kcp_host="$candidate"
    break
  fi
done < <(colima ssh -- ip -o -4 addr show scope global | awk '{split($4, address, "/"); print address[1]}')
if [[ -z "$kcp_host" ]]; then
  fail 'Colima has no macOS-reachable VM address for UDP KCP. Restart it with --network-address and rerun the gate.'
fi
{
  echo "colima_reachable_address=$kcp_host"
  echo "kcp_endpoint=$kcp_host:$kcp_port"
} >>"$metadata"

echo 'Running 36 exact-commit Unity EditMode tests...'
"$editor_path" \
  -batchmode \
  -nographics \
  -projectPath "$repo_root/client/UnityProject" \
  -runTests \
  -testPlatform EditMode \
  -testResults "$editmode_xml" \
  -logFile "$editmode_log"
assert_nunit_result "$editmode_xml" 36 'EditMode'

echo 'Running 2 real Fantasy KCP Unity PlayMode tests...'
AINATIVE_WS26_RUN_PLAYMODE=1 \
AINATIVE_WS26_HOST="$kcp_host" \
AINATIVE_WS26_PORT="$kcp_port" \
"$editor_path" \
  -batchmode \
  -nographics \
  -projectPath "$repo_root/client/UnityProject" \
  -runTests \
  -testPlatform PlayMode \
  -testResults "$playmode_xml" \
  -logFile "$playmode_log"
assert_nunit_result "$playmode_xml" 2 'PlayMode'

echo 'Building the macOS Apple Silicon Mono Player...'
mkdir -p "$player_directory"
"$editor_path" \
  -batchmode \
  -nographics \
  -quit \
  -projectPath "$repo_root/client/UnityProject" \
  -executeMethod AiNative.Client.Editor.BattleClientBuild.BuildMacOsArm64Smoke \
  --ainative-build-output "$player_bundle" \
  -logFile "$player_build_log"

[[ -s "$player_info_plist" ]] || fail "Unity did not produce the macOS Player Info.plist at: $player_info_plist"
player_executable_name="$(plutil -extract CFBundleExecutable raw "$player_info_plist")"
if [[ -z "$player_executable_name" || "$player_executable_name" == */* ]]; then
  fail "The macOS Player has an invalid CFBundleExecutable: $player_executable_name"
fi
player_executable="$player_bundle/Contents/MacOS/$player_executable_name"
[[ -x "$player_executable" ]] || fail "Unity did not produce the CFBundleExecutable at: $player_executable"
player_architectures="$(lipo -archs "$player_executable")"
[[ "$player_architectures" == 'arm64' ]] || fail "Expected an ARM64-only Player, found: $player_architectures"
player_notice="$player_directory/THIRD-PARTY-NOTICES.md"
player_license="$player_directory/Fantasy-LICENSE.txt"
[[ -s "$player_notice" && -s "$player_license" ]] || fail 'The macOS Player distribution is missing the Fantasy notice or license.'
cmp -s "$player_notice" "$fantasy_notice" || fail 'The staged third-party notice differs from the approved repository notice.'
cmp -s "$player_license" "$fantasy_license" || fail 'The staged Fantasy license differs from the approved pinned license.'

echo 'Running the deterministic macOS Player smoke...'
"$player_executable" \
  --ainative-smoke \
  --ainative-host "$kcp_host" \
  --ainative-port "$kcp_port" \
  --ainative-result "$smoke_json" \
  -batchmode \
  -nographics >"$player_stdout" 2>"$player_stderr" &
player_pid=$!
deadline=$((SECONDS + 120))
while kill -0 "$player_pid" >/dev/null 2>&1; do
  if (( SECONDS >= deadline )); then
    kill -TERM "$player_pid" >/dev/null 2>&1 || true
    sleep 2
    if kill -0 "$player_pid" >/dev/null 2>&1; then
      kill -KILL "$player_pid" >/dev/null 2>&1 || true
    fi
    wait "$player_pid" >/dev/null 2>&1 || true
    player_pid=''
    fail 'The macOS Player smoke timed out after 120 seconds.'
  fi
  sleep 1
done
set +e
wait "$player_pid"
player_exit=$?
set -e
player_pid=''
[[ "$player_exit" == '0' ]] || fail "The macOS Player exited with code $player_exit."
[[ -s "$smoke_json" ]] || fail 'The macOS Player did not produce smoke.json.'
jq -e '
  .success == true
  and (.sessionId | tonumber) > 0
  and .initialEpoch > 0
  and .reconnectedEpoch > .initialEpoch
  and .preReconnectAcknowledgedSequence > 0
  and .lastAcknowledgedSequence > .preReconnectAcknowledgedSequence
  and (.lastReceivedTick | tonumber) > 0
  and .droppedInputFrames == 0
' "$smoke_json" >/dev/null || fail 'The macOS smoke did not prove login, reconnect continuity, acknowledgement growth, and zero dropped input frames.'

echo 'Stopping the Battle Host normally...'
docker stop --time 30 "$container_id" >/dev/null
host_exit="$(docker inspect "$container_id" --format '{{.State.ExitCode}}')"
host_oom="$(docker inspect "$container_id" --format '{{.State.OOMKilled}}')"
docker logs "$container_id" >"$host_log" 2>&1
[[ "$host_exit" == '0' && "$host_oom" == 'false' ]] || fail "The Battle Host did not stop normally: exit=$host_exit oom=$host_oom"
grep -q 'Application is shutting down...' "$host_log" || fail 'The Battle Host log does not contain the application shutdown marker.'
grep -q 'Acceptance room set drained' "$host_log" || fail 'The Battle Host log does not prove that rooms drained.'
grep -q 'Fantasy KCP gateway drained' "$host_log" || fail 'The Battle Host log does not prove that the KCP gateway drained.'
docker rm "$container_id" >/dev/null
container_id=''

{
  echo 'result=Passed'
  echo 'editmode_passed=36'
  echo 'editmode_failed=0'
  echo 'editmode_skipped=0'
  echo 'playmode_passed=2'
  echo 'playmode_failed=0'
  echo 'playmode_skipped=0'
  echo 'player_target=StandaloneOSX'
  echo 'player_architecture=arm64'
  echo 'player_scripting_backend=Mono'
  echo "player_executable=$player_executable_name"
  echo 'smoke_exit_code=0'
  echo 'smoke_success=true'
  echo "smoke_session_id=$(jq -r '.sessionId' "$smoke_json")"
  echo "smoke_initial_epoch=$(jq -r '.initialEpoch' "$smoke_json")"
  echo "smoke_reconnected_epoch=$(jq -r '.reconnectedEpoch' "$smoke_json")"
  echo "smoke_pre_reconnect_ack=$(jq -r '.preReconnectAcknowledgedSequence' "$smoke_json")"
  echo "smoke_post_reconnect_ack=$(jq -r '.lastAcknowledgedSequence' "$smoke_json")"
  echo "smoke_last_received_tick=$(jq -r '.lastReceivedTick' "$smoke_json")"
  echo "smoke_dropped_input_frames=$(jq -r '.droppedInputFrames' "$smoke_json")"
  echo "battle_host_exit_code=$host_exit"
  echo 'battle_host_rooms_drained=true'
  echo 'battle_host_kcp_drained=true'
  echo 'battle_host_forced_termination=false'
} | tee "$summary"

(
  cd "$evidence_dir"
  shasum -a 256 \
    metadata.txt \
    summary.txt \
    Fantasy.config \
    editmode.xml \
    editmode.log \
    playmode.xml \
    playmode.log \
    battle-host-build.log \
    battle-host.log \
    player-build.log \
    player.stdout.log \
    player.stderr.log \
    smoke.json \
    player/THIRD-PARTY-NOTICES.md \
    player/Fantasy-LICENSE.txt \
    "player/AiNative.BattleClient.app/Contents/MacOS/$player_executable_name" \
    > hashes.sha256
)

post_validation_dirty="$(git -C "$repo_root" status --porcelain --untracked-files=all)"
[[ -z "$post_validation_dirty" ]] || fail "Validation changed the clean worktree: $post_validation_dirty"

trap - EXIT INT TERM
echo "macOS Unity validation passed. Evidence: $evidence_dir"

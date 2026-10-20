
˙&
ainative/v1/backend.protoainative.backend.v1"˜
ControlRequest%
correlation_id (	RcorrelationId
method (	Rmethod
body (Rbody

credential (	R
credential.
delegated_player_id (	RdelegatedPlayerId!
trace_parent (	RtraceParent
trace_state (	R
traceState"b
ControlResponse%
correlation_id (	RcorrelationId
body (Rbody
error (	Rerror"A
ControlNotification
method (	Rmethod
body (Rbody"(
PresenceUpdate
online (Ronline"D
PresenceState
	player_id (	RplayerId
online (Ronline"
Empty"H
AccountRequest
username (	Rusername
password (	Rpassword"Å
LoginResult
	player_id (	RplayerId#
session_token (	RsessionToken0
expires_unix_seconds (RexpiresUnixSeconds"5
SessionRequest#
session_token (	RsessionToken"_
PlayerIdentity
	player_id (	RplayerId0
expires_unix_seconds (RexpiresUnixSeconds"è
PlayerProfile
	player_id (	RplayerId!
display_name (	RdisplayName
played (Rplayed
won (Rwon
kills (Rkills"-
ProfileRequest
	player_id (	RplayerId"î
PartyCommand
party_id (	RpartyId(
target_player_id (	RtargetPlayerId
ready (Rready)
expected_version (RexpectedVersion"@
PartyMember
	player_id (	RplayerId
ready (Rready"ƒ

PartyState
party_id (	RpartyId
	leader_id (	RleaderId
version (Rversion:
members (2 .ainative.backend.v1.PartyMemberRmembers(
queue_request_id (	RqueueRequestId"$
ServiceEpoch
epoch (	Repoch"M
PartyInvitations9
parties (2.ainative.backend.v1.PartyStateRparties"x
PartyQueueRequest
party_id (	RpartyId)
expected_version (RexpectedVersion

request_id (	R	requestId"Å

QueueEntry

request_id (	R	requestId
party_id (	RpartyId#
party_version (RpartyVersion

player_ids (	R	playerIds0
expected_match_epoch (	RexpectedMatchEpochC
admission_expires_unix_seconds (RadmissionExpiresUnixSeconds"+

MatchQuery

request_id (	R	requestId"º
MatchStatus

request_id (	R	requestId
state (	Rstate
match_id (	RmatchIdC

allocation (2#.ainative.backend.v1.RoomAllocationR
allocation
failure (	Rfailure"M
AllocationRequest
match_id (	RmatchId

player_ids (	R	playerIds"Ä
	RoomQuery
room_id (	RroomId
match_id (	RmatchId
	page_size (RpageSize"
after_room_id (	RafterRoomId"˙
RoomAllocation#
allocation_id (	RallocationId
match_id (	RmatchId
room_id (	RroomId
node_id (	RnodeId

boot_epoch (	R	bootEpoch
address (	Raddress
state (	Rstate

player_ids (	R	playerIds0
created_unix_seconds	 (RcreatedUnixSeconds+
coordinator_epoch
 (	RcoordinatorEpoch)
authority_fenced (RauthorityFenced"≠
WorkerCapacity
	worker_id (RworkerId'
available_rooms (RavailableRooms+
mailbox_available (RmailboxAvailable(
last_tick_micros (RlastTickMicros"°
BattleNodeReport
node_id (	RnodeId

boot_epoch (	R	bootEpoch
address (	Raddress
draining (Rdraining%
outbox_healthy (RoutboxHealthy=
workers (2#.ainative.backend.v1.WorkerCapacityRworkers9
rooms (2#.ainative.backend.v1.RoomAllocationRrooms"V
RoomReservationC

allocation (2#.ainative.backend.v1.RoomAllocationR
allocation"ò
RoomCreation#
allocation_id (	RallocationId
room_id (	RroomId

boot_epoch (	R	bootEpoch+
coordinator_epoch (	RcoordinatorEpoch"Ø
RoomRelease#
allocation_id (	RallocationId
room_id (	RroomId

boot_epoch (	R	bootEpoch
reason (	Rreason+
coordinator_epoch (	RcoordinatorEpoch"á
RoomList9
rooms (2#.ainative.backend.v1.RoomAllocationRrooms

allocating (R
allocating 
next_room_id (	R
nextRoomId"Z
DrainRequest

boot_epoch (	R	bootEpoch+
coordinator_epoch (	RcoordinatorEpoch"^
CoordinatorFence+
coordinator_epoch (	RcoordinatorEpoch

boot_epoch (	R	bootEpoch"q
TicketRequest
	player_id (	RplayerIdC

allocation (2#.ainative.backend.v1.RoomAllocationR
allocation"W
EntryTicket
ticket (	Rticket0
expires_unix_seconds (RexpiresUnixSeconds"M
BattleAdmission!
entry_ticket (	RentryTicket
room_id (	RroomId"i

MatchReady8
status (2 .ainative.backend.v1.MatchStatusRstatus!
entry_ticket (	RentryTicket"S
PlayerResult
	player_id (	RplayerId
won (Rwon
kills (Rkills"÷
MatchResult
match_id (	RmatchId
room_id (	RroomId
node_id (	RnodeId

boot_epoch (	R	bootEpoch;
players (2!.ainative.backend.v1.PlayerResultRplayers

completion (	R
completion",
SettlementQuery
match_id (	RmatchId"o
SettlementReceipt
match_id (	RmatchId!
payload_hash (	RpayloadHash
	confirmed (R	confirmedB™AiNative.Protocol.Backend.V1bproto3
Û!
ainative/v1/gameplay.protoainative.v1"®
LoginRequest%
protocol_major (RprotocolMajor!
client_build (	RclientBuild!
entry_ticket (	RentryTicket$
global_room_id (	RglobalRoomIdJd»"¬
LoginResponse

session_id (R	sessionId)
connection_epoch (RconnectionEpoch$
global_room_id (	RglobalRoomId

boot_epoch (	R	bootEpoch
	room_tick (RroomTickJd»"^
JoinRoomRequest

session_id (R	sessionId%
requested_room (RrequestedRoomJd»"l
JoinRoomResponse
room_id (RroomId
	entity_id (RentityId
	tick_rate (RtickRateJd»"ƒ
InputCommand
	room_tick (RroomTick
sequence (Rsequence 
move_x_milli (R
moveXMilli 
move_y_milli (R
moveYMilli)
yaw_millidegrees (RyawMillidegrees
buttons (Rbuttons
	weapon_id (RweaponId$
look_yaw_milli (RlookYawMilli(
look_pitch_milli	 (RlookPitchMilliJd»"J

InputBatch5
commands (2.ainative.v1.InputCommandRcommandsJd»"∫
PlayerState
	entity_id (RentityId(
position_x_milli (RpositionXMilli(
position_y_milli (RpositionYMilli(
position_z_milli (RpositionZMilli)
yaw_millidegrees (RyawMillidegrees
health (Rhealth<
velocity_x_milli_per_second (RvelocityXMilliPerSecond<
velocity_y_milli_per_second (RvelocityYMilliPerSecond<
velocity_z_milli_per_second	 (RvelocityZMilliPerSecond
	weapon_id
 (RweaponId
armor (Rarmor
alive (Ralive
kills (Rkills-
pitch_millidegrees (RpitchMillidegreesJd»"©
PickupState
	pickup_id (RpickupId=
pickup_type (2.ainative.v1.ArenaPickupTypeR
pickupType(
position_x_milli (RpositionXMilli(
position_y_milli (RpositionYMilli(
position_z_milli (RpositionZMilli
active (Ractive!
respawn_tick (RrespawnTickJd»"÷
Snapshot%
protocol_major (RprotocolMajor
	room_tick (RroomTick#
baseline_tick (RbaselineTick2
players (2.ainative.v1.PlayerStateRplayers

state_hash (R	stateHashA
last_processed_input_sequence (RlastProcessedInputSequence=
match_phase (2.ainative.v1.ArenaMatchPhaseR
matchPhase'
remaining_ticks (RremainingTicks(
leader_entity_id	 (RleaderEntityId2
pickups
 (2.ainative.v1.PickupStateRpickupsJd»"ù
ArenaCombatEvent@

event_type (2!.ainative.v1.ArenaCombatEventTypeR	eventType

event_tick (R	eventTick(
source_entity_id (RsourceEntityId(
target_entity_id (RtargetEntityId7
	weapon_id (2.ainative.v1.ArenaWeaponIdRweaponId
damage (Rdamage(
position_x_milli (RpositionXMilli(
position_y_milli (RpositionYMilli(
position_z_milli	 (RpositionZMilliJd»" 
ReliableEvent
	room_tick (RroomTick
sequence (Rsequence

event_type (R	eventType
payload (Rpayload@
combat_event (2.ainative.v1.ArenaCombatEventRcombatEventJd»"ë
ReconnectRequest

session_id (R	sessionId)
connection_epoch (RconnectionEpoch,
last_received_tick (RlastReceivedTickJd»"ô
ReconnectResponse)
connection_epoch (RconnectionEpoch
resume_tick (R
resumeTick1
snapshot (2.ainative.v1.SnapshotRsnapshotJd»*Í
	MessageId
MESSAGE_ID_UNSPECIFIED 
MESSAGE_ID_LOGIN_REQUESTË
MESSAGE_ID_LOGIN_RESPONSEÈ!
MESSAGE_ID_JOIN_ROOM_REQUESTÚ"
MESSAGE_ID_JOIN_ROOM_RESPONSEÛ
MESSAGE_ID_INPUT_COMMANDÃ
MESSAGE_ID_SNAPSHOTÕ
MESSAGE_ID_RELIABLE_EVENTŒ
MESSAGE_ID_INPUT_BATCHœ!
MESSAGE_ID_RECONNECT_REQUEST∞	"
MESSAGE_ID_RECONNECT_RESPONSE±	*´
ArenaButton
ARENA_BUTTON_NONE 
ARENA_BUTTON_FIRE
ARENA_BUTTON_JUMP
ARENA_BUTTON_RELOAD
ARENA_BUTTON_NEXT_WEAPON 
ARENA_BUTTON_PREVIOUS_WEAPON*v
ArenaWeaponId
ARENA_WEAPON_NONE 
ARENA_WEAPON_MACHINEGUN
ARENA_WEAPON_SHOTGUN
ARENA_WEAPON_ROCKET*\
ArenaMatchPhase
ARENA_MATCH_WAITING 
ARENA_MATCH_ACTIVE
ARENA_MATCH_FINISHED*\
ArenaPickupType
ARENA_PICKUP_UNKNOWN 
ARENA_PICKUP_HEALTH
ARENA_PICKUP_ARMOR*ß
ArenaCombatEventType
ARENA_EVENT_FIRE 
ARENA_EVENT_HIT
ARENA_EVENT_KILL
ARENA_EVENT_RESPAWN
ARENA_EVENT_PICKUP
ARENA_EVENT_WEAPON_SWITCHB™AiNative.Protocol.V1bproto3
Â
ainative/v1/gate_envelope.protoainative.transport.v1"/
GateRequestEnvelope
payload (Rpayload"O
GateResponseEnvelope

error_code (R	errorCode
payload (RpayloadB!™AiNative.Protocol.Transport.V1bproto3
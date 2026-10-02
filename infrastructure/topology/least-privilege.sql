-- Supply passwords through a secret manager. Run as migration owner, never an application account.
-- psql variables: player_password, coordinator_password
CREATE ROLE ainative_player LOGIN PASSWORD :'player_password';
CREATE ROLE ainative_coordinator LOGIN PASSWORD :'coordinator_password';
CREATE ROLE ainative_migration NOLOGIN;
CREATE SCHEMA IF NOT EXISTS player AUTHORIZATION ainative_migration;
CREATE SCHEMA IF NOT EXISTS coordinator AUTHORIZATION ainative_migration;
REVOKE ALL ON SCHEMA player, coordinator FROM PUBLIC;
GRANT USAGE ON SCHEMA player TO ainative_player;
GRANT USAGE ON SCHEMA coordinator TO ainative_coordinator;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA player TO ainative_player;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA coordinator TO ainative_coordinator;
ALTER DEFAULT PRIVILEGES FOR ROLE ainative_migration IN SCHEMA player GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ainative_player;
ALTER DEFAULT PRIVILEGES FOR ROLE ainative_migration IN SCHEMA coordinator GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ainative_coordinator;

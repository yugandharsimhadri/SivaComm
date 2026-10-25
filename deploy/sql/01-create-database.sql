-- SivaComm: the one-time server setup — the application's role, its
-- database, its schema and every privilege it needs. Run as the postgres
-- superuser:
--
--     psql -U postgres -v app_password='<choose one>' -f 01-create-database.sql
--
-- Variables:
--
--   app_password  the role's password — REQUIRED for a server. The same value
--                 goes into Database:Password in appsettings.Production.json.
--   app_user      the login the application signs in as.   default sivacomm
--   db_name       the database it owns.                     default sivacomm
--   developer     set to anything for a developer machine: grants CREATEDB
--                 (the tests make a throwaway database each) and allows the
--                 password to default to sivacomm-dev. A server role never
--                 gets CREATEDB.
--
-- After this the database is empty. The tables come from either
--   * the installer (Install-SivaComm.ps1 runs SivaComm.Api.exe --migrate), or
--   * full-schema.sql / migrate.sql in the same folder, for a DBA who wants to
--     read and run the SQL themselves.
-- Both can be used, in either order: each skips what is already applied.
--
-- Safe to re-run: an existing role gets the password given, an existing
-- database and schema are kept, and the grants are re-applied.
--
-- The role OWNS the database and the comm schema. That is what lets the
-- application's migrations create and alter tables later, as this same role,
-- with no superuser involved after this script.
--
-- Each statement is a SELECT that produces the DDL and \gexec that runs it:
-- psql substitutes :'variables' in a query but not inside a $$ block, and
-- CREATE DATABASE refuses to run inside one anyway. format()'s %I quotes an
-- identifier and %L a literal, so a password needing quotes is escaped
-- rather than breaking the statement.

\set ON_ERROR_STOP on

\if :{?app_user}
\else
    \set app_user 'sivacomm'
\endif

\if :{?db_name}
\else
    \set db_name 'sivacomm'
\endif

\if :{?app_password}
\else
    \if :{?developer}
        \set app_password 'sivacomm-dev'
    \else
        -- An error, not \quit: \quit exits 0 and a calling script would carry on.
        DO $$ BEGIN RAISE EXCEPTION 'app_password is required: psql -U postgres -v app_password=''<choose one>'' -f <this file>'; END $$;
    \endif
\endif

\echo Role :app_user, database :db_name

-- ------------------------------------------------------------------- role

SELECT format('CREATE ROLE %I LOGIN PASSWORD %L', :'app_user', :'app_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'app_user')
\gexec

SELECT format('ALTER ROLE %I WITH LOGIN PASSWORD %L', :'app_user', :'app_password')
\gexec

\if :{?developer}
    SELECT format('ALTER ROLE %I CREATEDB', :'app_user')
    \gexec
\endif

-- --------------------------------------------------------------- database

SELECT format('CREATE DATABASE %I OWNER %I ENCODING ''UTF8'' TEMPLATE template0',
              :'db_name', :'app_user')
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = :'db_name')
\gexec

-- Ownership, in case the database existed already under someone else.
SELECT format('ALTER DATABASE %I OWNER TO %I', :'db_name', :'app_user')
\gexec

SELECT format('GRANT ALL PRIVILEGES ON DATABASE %I TO %I', :'db_name', :'app_user')
\gexec

-- Nobody else may connect. (PostgreSQL grants CONNECT to PUBLIC by default.)
SELECT format('REVOKE CONNECT ON DATABASE %I FROM PUBLIC', :'db_name')
\gexec

-- ----------------------------------------------------------------- schema

\connect :db_name

-- Every SivaComm table lives in schema comm. Created here, owned by the
-- application role, so the migrations can create tables in it.
SELECT format('CREATE SCHEMA IF NOT EXISTS comm AUTHORIZATION %I', :'app_user')
\gexec

SELECT format('ALTER SCHEMA comm OWNER TO %I', :'app_user')
\gexec

SELECT format('GRANT ALL ON SCHEMA comm TO %I', :'app_user')
\gexec

-- END-OF-SETUP  (deploy\New-SchemaScripts.ps1 cuts here to build full-schema.sql)

\echo
\echo Done. Next: Database:Password in appsettings.Production.json, then Install-SivaComm.ps1
\echo (it creates the tables), or run full-schema.sql to create them now.

ALTER TABLE "AspNetUsers" ADD COLUMN "AccountDetail" JSONB NOT NULL DEFAULT '{}'::JSONB;

ALTER TABLE versions ADD COLUMN reviews JSONB NOT NULL DEFAULT '{}'::JSONB;

from fastapi import APIRouter, Depends
from sqlalchemy.orm import Session

from database import get_db
from dependencies import verify_client_key
from schemas import (
    MemberLoginRequest, MemberLoginResponse,
    MemberLogoutRequest, MemberLogoutResponse,
    MemberStatusResponse,
    MemberChangePasswordRequest, MemberChangePasswordResponse,
    MemberRedeemPointsRequest, MemberRedeemPointsResponse,
)
from services.membership_service import MembershipService

router = APIRouter(prefix="/api/member", tags=["member"])

_ClientAuth = Depends(verify_client_key)

# NOTE: self-service registration (POST /api/member/register) was removed.
# Member accounts are now created only by an admin via the dashboard
# (see dashboard/routes.py: POST /dashboard/api/membership/create-member),
# which issues a temp password and sets must_change_password=True.


@router.post("/login", response_model=MemberLoginResponse, dependencies=[_ClientAuth])
def login_member(req: MemberLoginRequest, db: Session = Depends(get_db)):
    svc = MembershipService(db)
    result = svc.login_member(req.pc_number, req.username, req.password)
    return MemberLoginResponse(**result)


@router.post("/logout", response_model=MemberLogoutResponse, dependencies=[_ClientAuth])
def logout_member(req: MemberLogoutRequest, db: Session = Depends(get_db)):
    svc = MembershipService(db)
    result = svc.logout_member(req.pc_number)
    return MemberLogoutResponse(**result)


@router.get("/status/{pc_number}", response_model=MemberStatusResponse, dependencies=[_ClientAuth])
def member_status(pc_number: int, db: Session = Depends(get_db)):
    svc = MembershipService(db)
    result = svc.get_member_status(pc_number)
    return MemberStatusResponse(**result)


@router.post("/change-password", response_model=MemberChangePasswordResponse, dependencies=[_ClientAuth])
def change_password(req: MemberChangePasswordRequest, db: Session = Depends(get_db)):
    svc = MembershipService(db)
    result = svc.change_password(req.pc_number, req.new_password)
    return MemberChangePasswordResponse(**result)


@router.post("/redeem-points", response_model=MemberRedeemPointsResponse, dependencies=[_ClientAuth])
def redeem_points(req: MemberRedeemPointsRequest, db: Session = Depends(get_db)):
    svc = MembershipService(db)
    result = svc.redeem_points(req.pc_number, req.points)
    return MemberRedeemPointsResponse(**result)
